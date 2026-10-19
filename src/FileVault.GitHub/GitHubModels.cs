namespace FileVault.GitHub;

/// <summary>Which repository and branch to work with, and how to reach GitHub.</summary>
public sealed class GitHubRepositoryOptions
{
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public required string Branch { get; init; }

    /// <summary>An OAuth or GitHub App user token; null reads public repositories only (60 requests an hour).</summary>
    public string? Token { get; init; }

    public Uri ApiBaseUri { get; init; } = new("https://api.github.com/");

    /// <summary>Where Git LFS lives: <c>&lt;web&gt;/&lt;owner&gt;/&lt;repo&gt;.git/info/lfs</c>.</summary>
    public Uri WebBaseUri { get; init; } = new("https://github.com/");

    /// <summary>
    /// How long a read of the branch head is reused before listing asks GitHub again. Writes always start from
    /// a fresh head.
    /// </summary>
    public TimeSpan HeadCacheDuration { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A commit of the branch, the state every listing and read of one moment is taken from.</summary>
public sealed record GitHubSnapshot(string CommitSha, string TreeSha, DateTimeOffset Date);

public enum GitHubEntryKind
{
    File,
    Folder
}

/// <summary>
/// A file or folder of a tree. <see cref="Path"/> is relative to the repository root with <c>/</c>.
/// For a Git LFS file <see cref="Size"/> is the object's size, not the pointer's.
/// </summary>
public sealed record GitHubEntry(string Path, GitHubEntryKind Kind, string Sha, string Mode, long Size, bool IsLfs)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

public enum GitHubWriteStatus
{
    Written,

    /// <summary>The file is not what the caller opened any more: nothing was written.</summary>
    ChangedMeanwhile,

    /// <summary>The path is tracked by Git LFS, which FileVault does not write.</summary>
    StoredInLfs
}

/// <param name="CommitSha">The new commit when <see cref="Status"/> is <see cref="GitHubWriteStatus.Written"/>.</param>
/// <param name="BlobSha">The file's blob now: the written one, or what is there instead when it changed meanwhile.</param>
public sealed record GitHubWriteResult(GitHubWriteStatus Status, string? CommitSha, string? BlobSha);

/// <summary>
/// One file of a commit made with <see cref="GitHubRepository.CommitAsync(IReadOnlyList{GitHubFileEdit}, string, CancellationToken)"/>:
/// what it becomes, and what it has to be at the branch head for the commit to go ahead.
/// </summary>
public sealed class GitHubFileEdit
{
    private GitHubFileEdit(string path, string? expectedSha, byte[]? content, GitHubEntry? earlier)
    {
        Path = path;
        ExpectedSha = expectedSha;
        Content = content;
        Earlier = earlier;
    }

    public string Path { get; }

    /// <summary>The blob the file must have at the head; null: there must be no file at <see cref="Path"/>.</summary>
    public string? ExpectedSha { get; }

    internal byte[]? Content { get; }
    internal GitHubEntry? Earlier { get; }
    internal bool IsDelete => Content is null && Earlier is null;

    public static GitHubFileEdit Write(string path, byte[] content, string? expectedSha) =>
        new(path, expectedSha, content, null);

    /// <summary>
    /// Puts back a file as some snapshot had it, reusing its blob: nothing is uploaded, and a Git LFS file keeps its
    /// pointer.
    /// </summary>
    public static GitHubFileEdit Restore(string path, GitHubEntry earlier, string? expectedSha) =>
        earlier.Kind == GitHubEntryKind.File
            ? new(path, expectedSha, null, earlier)
            : throw new ArgumentException($"'{earlier.Path}' is a folder.", nameof(earlier));

    public static GitHubFileEdit Delete(string path, string expectedSha) => new(path, expectedSha, null, null);
}

public enum GitHubCommitStatus
{
    Committed,

    /// <summary>Every file already is what the edits make it.</summary>
    NothingToCommit,

    /// <summary>Some files are not what the caller expected any more: nothing was written.</summary>
    ChangedMeanwhile,

    /// <summary>Some edits write content to paths tracked by Git LFS, which FileVault does not write.</summary>
    StoredInLfs
}

/// <param name="CommitSha">The new commit when <see cref="Status"/> is <see cref="GitHubCommitStatus.Committed"/>.</param>
/// <param name="Paths">
/// The files committed; the ones in the way when <see cref="Status"/> is <see cref="GitHubCommitStatus.ChangedMeanwhile"/>
/// or <see cref="GitHubCommitStatus.StoredInLfs"/>.
/// </param>
public sealed record GitHubCommitResult(GitHubCommitStatus Status, string? CommitSha, IReadOnlyList<string> Paths);

/// <summary>
/// A file that differs between two snapshots: null <see cref="Before"/> is a file added, null <see cref="After"/> one
/// removed. A Git LFS file has the size of its pointer here.
/// </summary>
public sealed record GitHubTreeChange(string Path, GitHubEntry? Before, GitHubEntry? After);

/// <summary>The branch kept moving under every attempt to commit on top of it.</summary>
public sealed class GitHubConcurrencyException(string message) : IOException(message);

public sealed record GitHubRepositoryInfo(string DefaultBranch, bool IsPrivate);

public sealed record GitHubBranch(string Name, string Sha);

/// <param name="AheadBy">Commits the head has that the base does not.</param>
/// <param name="BehindBy">Commits the base has that the head does not.</param>
public sealed record GitHubComparison(string Status, int AheadBy, int BehindBy);

public enum GitHubFileStatus
{
    Added,
    Modified,
    Removed,
    Renamed,
    Copied,
    Changed,
    Unchanged
}

/// <param name="PreviousPath">The old path of a renamed file.</param>
public sealed record GitHubCommitFile(string Path, GitHubFileStatus Status, string? PreviousPath);

/// <param name="AuthorLogin">The GitHub account of the author, when GitHub could match one.</param>
/// <param name="Files">Null in a list of commits; the files of a single commit (the first 300) otherwise.</param>
public sealed record GitHubCommit(
    string Sha,
    IReadOnlyList<string> ParentShas,
    string Message,
    string? AuthorName,
    string? AuthorEmail,
    string? AuthorLogin,
    DateTimeOffset AuthorDate,
    DateTimeOffset CommitterDate,
    IReadOnlyList<GitHubCommitFile>? Files);
