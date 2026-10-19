using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FileVault.Core;

namespace FileVault.GitHub;

/// <summary>
/// One branch of a GitHub repository: its files as of the branch head, writes as single commits on top of it, and
/// the history of the branch and of its files. Items and providers created from one instance count as the same
/// provider, so copies and moves between them stay on GitHub.
/// </summary>
public sealed class GitHubRepository
{
    /// <summary>The Contents and Git Data APIs refuse blobs over 100 MB.</summary>
    public const long MaxBlobBytes = 100L * 1024 * 1024;

    private const string FileMode = "100644";
    private const string TreeMode = "040000";
    private const int MaxCommitAttempts = 3;
    private const int ParallelPointerReads = 8;

    private readonly GitHubRepositoryOptions _options;
    private readonly ConcurrentDictionary<string, ApiTree> _trees = new();
    private readonly ConcurrentDictionary<string, GitLfsPointer?> _pointers = new();
    private readonly ConcurrentDictionary<string, GitAttributes> _attributes = new();
    private readonly SemaphoreSlim _headGate = new(1, 1);
    private GitHubSnapshot? _head;
    private DateTimeOffset _headReadAt;

    public GitHubRepository(GitHubRepositoryOptions options, HttpClient httpClient)
    {
        _options = options;
        Api = new GitHubApi(httpClient, options.Token, options.ApiBaseUri, options.WebBaseUri, options.Owner,
            options.Name);
    }

    public string Owner => _options.Owner;
    public string Name => _options.Name;
    public string Branch => _options.Branch;

    internal GitHubApi Api { get; }

    public string FullName(string path) => GitHubPath.Format(Owner, Name, Branch, path);

    /// <summary>A provider for a folder of the branch; <paramref name="path"/> empty is the root.</summary>
    public GitHubFileProvider CreateProvider(string path = "") => new(this, path);

    // ── Reading ──────────────────────────────────────────────────────────────

    public async Task<GitHubSnapshot> GetHeadAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _headGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!refresh && _head is not null && DateTimeOffset.UtcNow - _headReadAt < _options.HeadCacheDuration)
                return _head;

            var branchRef = await Api.GetAsync(Api.Repo($"git/ref/heads/{GitHubApi.EscapePath(Branch)}"),
                GitHubJsonContext.Default.ApiRef, ct).ConfigureAwait(false);

            return Remember(await ReadSnapshotAsync(branchRef.Object.Sha, ct).ConfigureAwait(false));
        }
        finally
        {
            _headGate.Release();
        }
    }

    /// <summary>The snapshot of any commit, e.g. to read a file as a past version had it.</summary>
    public async Task<GitHubSnapshot> GetSnapshotAsync(string commitSha, CancellationToken ct = default) =>
        await ReadSnapshotAsync(commitSha, ct).ConfigureAwait(false);

    /// <summary>
    /// The files and folders of <paramref name="folderPath"/>; throws <see cref="DirectoryNotFoundException"/>
    /// when there is no such folder.
    /// </summary>
    public async Task<IReadOnlyList<GitHubEntry>> ListAsync(string folderPath, GitHubSnapshot? at = null,
        CancellationToken ct = default)
    {
        var snapshot = at ?? await GetHeadAsync(ct: ct).ConfigureAwait(false);
        var tree = await FindTreeAsync(snapshot.TreeSha, folderPath, ct).ConfigureAwait(false)
                   ?? throw new DirectoryNotFoundException($"There is no folder '{folderPath}' in {FullName("")}.");
        var attributes = await GetAttributesAsync(snapshot, ct).ConfigureAwait(false);

        var entries = tree.Tree
            .Where(e => e.Type is "blob" or "tree")
            .Select(e => ToEntry(GitHubPath.Combine(folderPath, e.Path), e, attributes))
            .ToList();

        return await WithLfsSizesAsync(entries, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every file and folder under <paramref name="folderPath"/> at any depth, each folder before what it holds, read
    /// in one request for the whole subtree. When GitHub cuts that answer short (over 100 000 entries or 7 MB), the
    /// subtree is read folder by folder instead. The folders read are cached, so listing anything below afterwards
    /// needs no request. <paramref name="resolveLfsSizes"/> false leaves an LFS file with its pointer's size
    /// (<see cref="GitHubEntry.IsLfs"/> still set) and spares a blob read per LFS file not seen before. Throws
    /// <see cref="DirectoryNotFoundException"/> when there is no such folder.
    /// </summary>
    public async Task<IReadOnlyList<GitHubEntry>> ListRecursiveAsync(string folderPath, GitHubSnapshot? at = null,
        bool resolveLfsSizes = true, CancellationToken ct = default)
    {
        var snapshot = at ?? await GetHeadAsync(ct: ct).ConfigureAwait(false);
        var treeSha = folderPath.Length == 0
            ? snapshot.TreeSha
            : await FindEntryAsync(snapshot.TreeSha, folderPath, ct).ConfigureAwait(false) is { Type: "tree" } folder
                ? folder.Sha
                : throw new DirectoryNotFoundException($"There is no folder '{folderPath}' in {FullName("")}.");

        var found = new List<(string Path, ApiTreeEntry Entry)>();
        await CollectSubtreeAsync(treeSha, folderPath, found, ct).ConfigureAwait(false);
        var attributes = await GetAttributesAsync(snapshot, ct).ConfigureAwait(false);

        var entries = found
            .Where(f => f.Entry.Type is "blob" or "tree")
            .Select(f => ToEntry(f.Path, f.Entry, attributes))
            .ToList();

        return resolveLfsSizes ? await WithLfsSizesAsync(entries, ct).ConfigureAwait(false) : entries;
    }

    /// <summary>The entry at <paramref name="path"/>, or null when there is none.</summary>
    public async Task<GitHubEntry?> FindAsync(string path, GitHubSnapshot? at = null, CancellationToken ct = default)
    {
        if (path.Length == 0)
            return null;

        var snapshot = at ?? await GetHeadAsync(ct: ct).ConfigureAwait(false);

        return (await FindEntryAsync(snapshot.TreeSha, path, ct).ConfigureAwait(false)) is { } entry
            ? ToEntry(path, entry, await GetAttributesAsync(snapshot, ct).ConfigureAwait(false))
            : null;
    }

    /// <summary>
    /// The content of a file, with a Git LFS pointer replaced by the object it points to. The content is downloaded
    /// to a temporary file first, so a broken transfer fails here instead of looking like a short file.
    /// </summary>
    public async Task<Stream> OpenReadAsync(GitHubEntry file, CancellationToken ct = default)
    {
        if (file.Kind != GitHubEntryKind.File)
            throw new ArgumentException($"'{file.Path}' is a folder.", nameof(file));

        return await TemporaryFileBuffer.CreateAsync(async (destination, token) =>
        {
            await Api.DownloadBlobAsync(file.Sha, destination, token).ConfigureAwait(false);

            if (destination.Length > GitLfsPointer.MaxPointerSize)
                return;

            var head = new byte[destination.Length];
            destination.Position = 0;
            await destination.ReadExactlyAsync(head, token).ConfigureAwait(false);

            if (GitLfsPointer.TryParse(head) is not { } pointer)
                return;

            _pointers[file.Sha] = pointer;
            destination.SetLength(0);
            await Api.DownloadLfsObjectAsync(pointer, destination, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>A file as it was in <paramref name="commitSha"/>; null when it did not exist there.</summary>
    public async Task<Stream?> OpenReadAtAsync(string commitSha, string path, CancellationToken ct = default)
    {
        var snapshot = await ReadSnapshotAsync(commitSha, ct).ConfigureAwait(false);

        return await FindAsync(path, snapshot, ct).ConfigureAwait(false) is { Kind: GitHubEntryKind.File } file
            ? await OpenReadAsync(file, ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>The LFS rules of the root <c>.gitattributes</c> of a snapshot.</summary>
    public async Task<GitAttributes> GetAttributesAsync(GitHubSnapshot snapshot, CancellationToken ct = default)
    {
        if (_attributes.TryGetValue(snapshot.TreeSha, out var cached))
            return cached;

        var root = await GetTreeAsync(snapshot.TreeSha, ct).ConfigureAwait(false);
        var file = root.Tree.FirstOrDefault(e => e is { Path: ".gitattributes", Type: "blob" });
        var attributes = GitAttributes.Empty;

        if (file is not null)
        {
            using var buffer = new MemoryStream();
            await Api.DownloadBlobAsync(file.Sha, buffer, ct).ConfigureAwait(false);
            attributes = GitAttributes.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        return _attributes[snapshot.TreeSha] = attributes;
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a text or other small file as one commit. With <paramref name="expectedBlobSha"/> the write only happens
    /// while the file is still the one the caller opened (null: the file must not exist yet); otherwise nothing is
    /// written and <see cref="GitHubWriteStatus.ChangedMeanwhile"/> comes back with what is there now.
    /// </summary>
    public async Task<GitHubWriteResult> WriteFileAsync(string path, byte[] content, string? expectedBlobSha,
        string message, CancellationToken ct = default)
    {
        if (!GitHubPath.IsValidPath(path))
            throw new ArgumentException($"'{path}' is not a valid path.", nameof(path));

        var head = await GetHeadAsync(refresh: true, ct).ConfigureAwait(false);

        if ((await GetAttributesAsync(head, ct).ConfigureAwait(false)).IsLfs(path))
            return new GitHubWriteResult(GitHubWriteStatus.StoredInLfs, null, null);

        string? current = null;
        var changedMeanwhile = false;

        var commit = await CommitAsync([GitHubChange.Put(path, content)], message, async (snapshot, token) =>
        {
            current = (await FindAsync(path, snapshot, token).ConfigureAwait(false))?.Sha;
            changedMeanwhile = current != expectedBlobSha;
            return !changedMeanwhile;
        }, ct).ConfigureAwait(false);

        return changedMeanwhile
            ? new GitHubWriteResult(GitHubWriteStatus.ChangedMeanwhile, null, current)
            : new GitHubWriteResult(GitHubWriteStatus.Written, commit, BlobSha(content));
    }

    /// <summary>The git blob id of some content: what <see cref="GitHubEntry.Sha"/> becomes once it is written.</summary>
    public static string BlobSha(ReadOnlySpan<byte> content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var data = new byte[header.Length + content.Length];
        header.CopyTo(data, 0);
        content.CopyTo(data.AsSpan(header.Length));

        return Convert.ToHexStringLower(SHA1.HashData(data));
    }

    /// <summary>
    /// Applies <paramref name="changes"/> as one commit on top of the branch head and moves the branch to it without
    /// force. When someone else moved the branch meanwhile, the changes are applied again on the new head, so a
    /// commit never drops another one. <paramref name="precondition"/> runs against every head tried and stops the
    /// write when it returns false. Returns the new commit, or null when nothing changed.
    /// </summary>
    internal async Task<string?> CommitAsync(IReadOnlyList<GitHubChange> changes, string message,
        Func<GitHubSnapshot, CancellationToken, Task<bool>>? precondition, CancellationToken ct)
    {
        var blobs = new Dictionary<GitHubChange, string>();

        for (var attempt = 0; attempt < MaxCommitAttempts; attempt++)
        {
            var head = await GetHeadAsync(refresh: true, ct).ConfigureAwait(false);

            if (precondition is not null && !await precondition(head, ct).ConfigureAwait(false))
                return null;

            var entries = new List<ApiNewTreeEntry>();

            foreach (var change in changes)
                entries.AddRange(await ResolveAsync(change, head, blobs, ct).ConfigureAwait(false));

            if (entries.Count == 0)
                return null;

            var tree = await Api.PostAsync(Api.Repo("git/trees"), new ApiNewTree(head.TreeSha, entries),
                GitHubJsonContext.Default.ApiNewTree, GitHubJsonContext.Default.ApiCreatedBlob, ct).ConfigureAwait(false);

            if (tree.Sha == head.TreeSha)
                return null;

            var commit = await Api.PostAsync(Api.Repo("git/commits"),
                new ApiNewCommit(message, tree.Sha, [head.CommitSha]),
                GitHubJsonContext.Default.ApiNewCommit, GitHubJsonContext.Default.ApiGitCommit, ct).ConfigureAwait(false);

            try
            {
                await Api.PatchAsync(Api.Repo($"git/refs/heads/{GitHubApi.EscapePath(Branch)}"),
                    new ApiUpdateRef(commit.Sha, Force: false),
                    GitHubJsonContext.Default.ApiUpdateRef, GitHubJsonContext.Default.ApiRef, ct).ConfigureAwait(false);
            }
            catch (GitHubApiException e) when (e.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
            {
                // Not a fast-forward: the branch moved after we read it
                continue;
            }

            await _headGate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                Remember(new GitHubSnapshot(commit.Sha, tree.Sha, commit.Committer.Date));
            }
            finally
            {
                _headGate.Release();
            }

            return commit.Sha;
        }

        throw new GitHubConcurrencyException(
            $"The branch '{Branch}' kept changing while committing; nothing was written.");
    }

    private async Task<IEnumerable<ApiNewTreeEntry>> ResolveAsync(GitHubChange change, GitHubSnapshot head,
        Dictionary<GitHubChange, string> blobs, CancellationToken ct)
    {
        switch (change.Kind)
        {
            case GitHubChangeKind.Put:
                if (!blobs.TryGetValue(change, out var blob))
                {
                    var created = await Api.PostAsync(Api.Repo("git/blobs"),
                        new ApiNewBlob(Convert.ToBase64String(change.Content!), "base64"),
                        GitHubJsonContext.Default.ApiNewBlob, GitHubJsonContext.Default.ApiCreatedBlob, ct)
                        .ConfigureAwait(false);
                    blobs[change] = blob = created.Sha;
                }

                return [new ApiNewTreeEntry(change.Path, FileMode, "blob", blob)];

            case GitHubChangeKind.Copy:
            case GitHubChangeKind.Move:
                var source = await FindEntryAsync(head.TreeSha, change.From!, ct).ConfigureAwait(false)
                             ?? throw new FileNotFoundException($"'{change.From}' is not in {FullName("")} any more.");
                var copy = new ApiNewTreeEntry(change.Path, source.Mode, source.Type, source.Sha);

                return change.Kind == GitHubChangeKind.Copy
                    ? [copy]
                    : [copy, .. await DeletionsAsync(change.From!, head, ct).ConfigureAwait(false)];

            default:
                return await DeletionsAsync(change.Path, head, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tree entries that delete a file, or every file of a folder: a null sha removes a blob, and a folder left empty
    /// disappears by itself.
    /// </summary>
    private async Task<IEnumerable<ApiNewTreeEntry>> DeletionsAsync(string path, GitHubSnapshot head,
        CancellationToken ct)
    {
        var entry = await FindEntryAsync(head.TreeSha, path, ct).ConfigureAwait(false);

        if (entry is null)
            return [];

        if (entry.Type != "tree")
            return [new ApiNewTreeEntry(path, entry.Mode, entry.Type, null)];

        var all = await Api.GetAsync(Api.Repo($"git/trees/{entry.Sha}?recursive=1"), GitHubJsonContext.Default.ApiTree, ct)
            .ConfigureAwait(false);

        if (all.Truncated)
            throw new NotSupportedException($"The folder '{path}' is too big to delete in one commit.");

        return all.Tree
            .Where(e => e.Type != "tree")
            .Select(e => new ApiNewTreeEntry(GitHubPath.Combine(path, e.Path), e.Mode, e.Type, null));
    }

    // ── History ─────────────────────────────────────────────────────────────

    public async Task<GitHubRepositoryInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var repository = await Api.GetAsync(Api.Repo(""), GitHubJsonContext.Default.ApiRepository, ct)
            .ConfigureAwait(false);

        return new GitHubRepositoryInfo(repository.DefaultBranch, repository.Private);
    }

    /// <summary>
    /// Commits newest first, from <paramref name="fromRef"/> (the branch by default), only those touching
    /// <paramref name="path"/> when it is given. Pages start at 1; GitHub gives at most 100 per page.
    /// </summary>
    public async Task<IReadOnlyList<GitHubCommit>> GetCommitsAsync(string? path = null, string? fromRef = null,
        int perPage = 30, int page = 1, CancellationToken ct = default)
    {
        var query = $"commits?sha={Uri.EscapeDataString(fromRef ?? Branch)}&per_page={Math.Clamp(perPage, 1, 100)}&page={Math.Max(page, 1)}";

        if (!string.IsNullOrEmpty(path))
            query += $"&path={Uri.EscapeDataString(path)}";

        var commits = await Api.GetAsync(Api.Repo(query), GitHubJsonContext.Default.ListApiCommit, ct)
            .ConfigureAwait(false);

        return commits.Select(ToCommit).ToList();
    }

    /// <summary>One commit with the files it changed; null when there is no such commit.</summary>
    public async Task<GitHubCommit?> GetCommitAsync(string sha, CancellationToken ct = default) =>
        await Api.TryGetAsync(Api.Repo($"commits/{Uri.EscapeDataString(sha)}"), GitHubJsonContext.Default.ApiCommit, ct)
            .ConfigureAwait(false) is { } commit
            ? ToCommit(commit)
            : null;

    public async Task<IReadOnlyList<GitHubBranch>> GetBranchesAsync(CancellationToken ct = default)
    {
        var branches = new List<GitHubBranch>();

        for (var page = 1; ; page++)
        {
            var batch = await Api.GetAsync(Api.Repo($"branches?per_page=100&page={page}"),
                GitHubJsonContext.Default.ListApiBranch, ct).ConfigureAwait(false);

            branches.AddRange(batch.Select(b => new GitHubBranch(b.Name, b.Commit.Sha)));

            if (batch.Count < 100)
                return branches;
        }
    }

    /// <summary>How <paramref name="head"/> relates to <paramref name="baseRef"/>: e.g. a cloud agent's branch to the main one.</summary>
    public async Task<GitHubComparison> CompareAsync(string baseRef, string head, CancellationToken ct = default)
    {
        var comparison = await Api.GetAsync(
            Api.Repo($"compare/{Uri.EscapeDataString(baseRef)}...{Uri.EscapeDataString(head)}?per_page=1"),
            GitHubJsonContext.Default.ApiComparison, ct).ConfigureAwait(false);

        return new GitHubComparison(comparison.Status, comparison.AheadBy, comparison.BehindBy);
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private GitHubSnapshot Remember(GitHubSnapshot snapshot)
    {
        _head = snapshot;
        _headReadAt = DateTimeOffset.UtcNow;
        return snapshot;
    }

    private async Task<GitHubSnapshot> ReadSnapshotAsync(string commitSha, CancellationToken ct)
    {
        var commit = await Api.GetAsync(Api.Repo($"git/commits/{Uri.EscapeDataString(commitSha)}"),
            GitHubJsonContext.Default.ApiGitCommit, ct).ConfigureAwait(false);

        return new GitHubSnapshot(commit.Sha, commit.Tree.Sha, commit.Committer.Date);
    }

    // Trees are addressed by content, so a cached one never goes stale
    private async Task<ApiTree> GetTreeAsync(string sha, CancellationToken ct)
    {
        if (_trees.TryGetValue(sha, out var tree))
            return tree;

        tree = await Api.GetAsync(Api.Repo($"git/trees/{sha}"), GitHubJsonContext.Default.ApiTree, ct)
            .ConfigureAwait(false);

        return _trees[sha] = tree;
    }

    /// <summary>
    /// The entries under a tree with their paths, folders before their content. From the cache when every folder
    /// below is there; otherwise one recursive request, or a walk when GitHub truncates it.
    /// </summary>
    private async Task CollectSubtreeAsync(string sha, string prefix, List<(string Path, ApiTreeEntry Entry)> found,
        CancellationToken ct)
    {
        var start = found.Count;

        if (TryCollectCached(sha, prefix, found))
            return;

        found.RemoveRange(start, found.Count - start);

        var all = await Api.GetAsync(Api.Repo($"git/trees/{sha}?recursive=1"), GitHubJsonContext.Default.ApiTree, ct)
            .ConfigureAwait(false);

        if (!all.Truncated)
        {
            RememberSubtrees(sha, all.Tree);
            found.AddRange(all.Tree.Select(e => (GitHubPath.Combine(prefix, e.Path), e)));
            return;
        }

        // Over GitHub's limit for one answer: this level alone, then each folder below the same way
        foreach (var entry in (await GetTreeAsync(sha, ct).ConfigureAwait(false)).Tree)
        {
            var path = GitHubPath.Combine(prefix, entry.Path);
            found.Add((path, entry));

            if (entry.Type == "tree")
                await CollectSubtreeAsync(entry.Sha, path, found, ct).ConfigureAwait(false);
        }
    }

    private bool TryCollectCached(string sha, string prefix, List<(string Path, ApiTreeEntry Entry)> found)
    {
        if (!_trees.TryGetValue(sha, out var tree))
            return false;

        foreach (var entry in tree.Tree)
        {
            var path = GitHubPath.Combine(prefix, entry.Path);
            found.Add((path, entry));

            if (entry.Type == "tree" && !TryCollectCached(entry.Sha, path, found))
                return false;
        }

        return true;
    }

    /// <summary>A whole recursive listing holds every tree below it in full: cache each one as a plain listing would.</summary>
    private void RememberSubtrees(string rootSha, List<ApiTreeEntry> all)
    {
        var children = new Dictionary<string, List<ApiTreeEntry>> { [""] = [] };
        var shas = new Dictionary<string, string> { [""] = rootSha };

        foreach (var entry in all)
        {
            var slash = entry.Path.LastIndexOf('/');
            var parent = slash < 0 ? "" : entry.Path[..slash];

            if (!children.TryGetValue(parent, out var siblings))
                children[parent] = siblings = [];

            siblings.Add(entry with { Path = entry.Path[(slash + 1)..] });

            if (entry.Type == "tree")
            {
                shas[entry.Path] = entry.Sha;
                children.TryAdd(entry.Path, []);
            }
        }

        foreach (var (path, sha) in shas)
            _trees.TryAdd(sha, new ApiTree(sha, children[path], false));
    }

    private async Task<ApiTree?> FindTreeAsync(string rootSha, string path, CancellationToken ct)
    {
        if (path.Length == 0)
            return await GetTreeAsync(rootSha, ct).ConfigureAwait(false);

        return await FindEntryAsync(rootSha, path, ct).ConfigureAwait(false) is { Type: "tree" } entry
            ? await GetTreeAsync(entry.Sha, ct).ConfigureAwait(false)
            : null;
    }

    private async Task<ApiTreeEntry?> FindEntryAsync(string rootSha, string path, CancellationToken ct)
    {
        var tree = await GetTreeAsync(rootSha, ct).ConfigureAwait(false);
        var segments = path.Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            var entry = tree.Tree.FirstOrDefault(e => e.Path == segments[i]);

            if (entry is null || i == segments.Length - 1)
                return entry;

            if (entry.Type != "tree")
                return null;

            tree = await GetTreeAsync(entry.Sha, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static GitHubEntry ToEntry(string path, ApiTreeEntry entry, GitAttributes attributes) =>
        entry.Type == "tree"
            ? new GitHubEntry(path, GitHubEntryKind.Folder, entry.Sha, entry.Mode, 0, false)
            : new GitHubEntry(path, GitHubEntryKind.File, entry.Sha, entry.Mode, entry.Size ?? 0, attributes.IsLfs(path));

    /// <summary>
    /// A file tracked by LFS shows the pointer's size in its tree; the pointer itself is a tiny blob, read once per
    /// sha to learn the real size.
    /// </summary>
    private async Task<IReadOnlyList<GitHubEntry>> WithLfsSizesAsync(List<GitHubEntry> entries, CancellationToken ct)
    {
        await ResolveLfsSizesAsync(entries, ct).ConfigureAwait(false);

        return entries.Select(e => _pointers.TryGetValue(e.Sha, out var pointer) && pointer is not null
                ? e with { Size = pointer.Size, IsLfs = true }
                : e)
            .ToList();
    }

    private async Task ResolveLfsSizesAsync(IEnumerable<GitHubEntry> entries, CancellationToken ct)
    {
        var unknown = entries
            .Where(e => e is { Kind: GitHubEntryKind.File, IsLfs: true, Size: <= GitLfsPointer.MaxPointerSize }
                        && !_pointers.ContainsKey(e.Sha))
            .ToList();

        await Parallel.ForEachAsync(unknown,
            new ParallelOptions { MaxDegreeOfParallelism = ParallelPointerReads, CancellationToken = ct },
            async (entry, token) =>
            {
                using var buffer = new MemoryStream();
                await Api.DownloadBlobAsync(entry.Sha, buffer, token).ConfigureAwait(false);
                _pointers[entry.Sha] = GitLfsPointer.TryParse(buffer.ToArray());
            }).ConfigureAwait(false);
    }

    private static GitHubCommit ToCommit(ApiCommit commit) => new(
        commit.Sha,
        commit.Parents.Select(p => p.Sha).ToList(),
        commit.Commit.Message,
        commit.Commit.Author.Name,
        commit.Commit.Author.Email,
        commit.Author?.Login,
        commit.Commit.Author.Date,
        commit.Commit.Committer.Date,
        commit.Files?.Select(f => new GitHubCommitFile(f.Filename, ToStatus(f.Status), f.PreviousFilename)).ToList());

    private static GitHubFileStatus ToStatus(string status) => status switch
    {
        "added" => GitHubFileStatus.Added,
        "removed" => GitHubFileStatus.Removed,
        "renamed" => GitHubFileStatus.Renamed,
        "copied" => GitHubFileStatus.Copied,
        "changed" => GitHubFileStatus.Changed,
        "unchanged" => GitHubFileStatus.Unchanged,
        _ => GitHubFileStatus.Modified
    };
}

internal enum GitHubChangeKind
{
    Put,
    Copy,
    Move,
    Delete
}

/// <summary>
/// One change of a commit, resolved against the head it lands on: a copy or a move takes what is at
/// <see cref="From"/> then, not what it was when the caller listed it.
/// </summary>
internal sealed class GitHubChange
{
    private GitHubChange(GitHubChangeKind kind, string path, string? from, byte[]? content)
    {
        Kind = kind;
        Path = path;
        From = from;
        Content = content;
    }

    public GitHubChangeKind Kind { get; }
    public string Path { get; }
    public string? From { get; }
    public byte[]? Content { get; }

    public static GitHubChange Put(string path, byte[] content) => new(GitHubChangeKind.Put, path, null, content);
    public static GitHubChange Copy(string from, string to) => new(GitHubChangeKind.Copy, to, from, null);
    public static GitHubChange Move(string from, string to) => new(GitHubChangeKind.Move, to, from, null);
    public static GitHubChange Delete(string path) => new(GitHubChangeKind.Delete, path, null, null);
}
