using FileVault.Core;

namespace FileVault.GitHub;

/// <summary>
/// A folder of a GitHub branch. Every write is one commit on top of the branch head. Git keeps no empty folders,
/// so a new folder holds an empty <c>.gitkeep</c>. Deleting is always undoable from the history, so a delete to the
/// recycle bin is a plain delete commit. Files tracked by Git LFS are read through LFS but not written.
/// </summary>
public sealed class GitHubFileProvider(GitHubRepository repository, string path = "") : IFileProvider
{
    private const string KeepFileName = ".gitkeep";

    public GitHubRepository Repository => repository;
    public string Path => path;

    public string Name => path.Length == 0 ? repository.Name : path[(path.LastIndexOf('/') + 1)..];
    public string FullName => repository.FullName(path);

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var head = await repository.GetHeadAsync(ct: ct).ConfigureAwait(false);
        var entries = await repository.ListAsync(path, head, ct).ConfigureAwait(false);

        foreach (var entry in entries.Where(e => e.Kind == GitHubEntryKind.Folder).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (filter.ShowHiddenItems || !entry.Name.StartsWith('.'))
                yield return new GitHubFolderItem(repository, entry.Path, head.Date);
        }

        foreach (var entry in entries.Where(e => e.Kind == GitHubEntryKind.File).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (filter.ShowHiddenItems || !entry.Name.StartsWith('.'))
                yield return new GitHubFileItem(repository, entry, head.Date);
        }
    }

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            GitHubPath.EnsureValidName(name);
            var resolved = await ResolveFolderNameAsync(name, ct).ConfigureAwait(false);
            var folder = GitHubPath.Combine(path, resolved);

            await repository.CommitAsync([GitHubChange.Put(GitHubPath.Combine(folder, KeepFileName), [])],
                $"Create folder {folder}", null, ct).ConfigureAwait(false);

            return FileOperationResult<IFolderItem>.Success(new GitHubFolderItem(repository, folder, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name, CancellationToken ct = default)
    {
        try
        {
            GitHubPath.EnsureValidName(name);
            var resolved = await ResolveFileNameAsync(name, ct).ConfigureAwait(false);
            var file = GitHubPath.Combine(path, resolved);

            return FileOperationResult<IFileItem>.Success(await PutAsync(file, [], $"Create {file}", ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    /// <summary>
    /// A delete is a commit, and the history keeps what it removed: <paramref name="toRecycleBin"/> needs no trash
    /// of its own here.
    /// </summary>
    public async Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default)
    {
        try
        {
            var paths = items.Select(OwnPath).ToList();

            await repository.CommitAsync(paths.Select(GitHubChange.Delete).ToList(),
                paths.Count == 1 ? $"Delete {paths[0]}" : $"Delete {paths.Count} items", null, ct).ConfigureAwait(false);

            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Success(items);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName,
        CancellationToken ct = default)
    {
        try
        {
            GitHubPath.EnsureValidName(newName);
            var from = OwnPath(item);
            var to = GitHubPath.Combine(GitHubPath.Parent(from), newName);

            if (to == from)
                return FileOperationResult<bool>.Success(true);

            if (await repository.FindAsync(to, ct: ct).ConfigureAwait(false) is not null)
                throw new IOException($"'{newName}' already exists.");

            await repository.CommitAsync([GitHubChange.Move(from, to)], $"Rename {from} to {to}", null, ct)
                .ConfigureAwait(false);

            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var destination = ParseDestination(destinationPath);

            if (sourceItem is GitHubFileItem own && ReferenceEquals(own.Repository, repository))
            {
                await repository.CommitAsync([GitHubChange.Copy(own.Path, destination)], $"Copy {own.Path} to {destination}",
                    null, ct).ConfigureAwait(false);
                progress.Report(1);

                return FileOperationResult<IFileItem>.Success(await FindFileAsync(destination, ct).ConfigureAwait(false));
            }

            var content = await ReadAllAsync(sourceItem, progress, ct).ConfigureAwait(false);
            var item = await PutAsync(destination, content, $"Add {destination}", ct).ConfigureAwait(false);
            progress.Report(1);

            return FileOperationResult<IFileItem>.Success(item);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> MoveFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var destination = ParseDestination(destinationPath);

            if (sourceItem is GitHubFileItem own && ReferenceEquals(own.Repository, repository))
            {
                if (own.Path != destination)
                    await repository.CommitAsync([GitHubChange.Move(own.Path, destination)],
                        $"Move {own.Path} to {destination}", null, ct).ConfigureAwait(false);

                progress.Report(1);
                return FileOperationResult<IFileItem>.Success(await FindFileAsync(destination, ct).ConfigureAwait(false));
            }

            var copy = await CopyFileInAsync(sourceItem, destinationPath, progress, ct).ConfigureAwait(false);

            if (!copy.IsSuccess)
                return copy;

            await sourceItem.DeleteAsync(ct).ConfigureAwait(false);
            return copy;
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    /// <summary>Within the repository a folder copy is one tree entry pointing at the same tree; elsewhere — recurse.</summary>
    public async Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            if (sourceItem is not GitHubFolderItem own || !ReferenceEquals(own.Repository, repository))
                return FileOperationResult<IFolderItem?>.Success(null);

            var destination = ParseDestination(destinationPath);
            EnsureNotInside(own.Path, destination);

            await repository.CommitAsync([GitHubChange.Copy(own.Path, destination)], $"Copy {own.Path} to {destination}",
                null, ct).ConfigureAwait(false);
            progress.Report(1);

            return FileOperationResult<IFolderItem?>.Success(new GitHubFolderItem(repository, destination, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem,
        string destinationPath, IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            if (sourceItem is not GitHubFolderItem own || !ReferenceEquals(own.Repository, repository))
                return FileOperationResult<IFolderItem?>.Success(null);

            var destination = ParseDestination(destinationPath);
            EnsureNotInside(own.Path, destination);

            if (own.Path != destination)
                await repository.CommitAsync([GitHubChange.Move(own.Path, destination)],
                    $"Move {own.Path} to {destination}", null, ct).ConfigureAwait(false);

            progress.Report(1);
            return FileOperationResult<IFolderItem?>.Success(new GitHubFolderItem(repository, destination, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default) =>
        (await ListOwnAsync(ct).ConfigureAwait(false)).Any(e => e.Kind == GitHubEntryKind.File && e.Name == fileName);

    public async Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default) =>
        (await ListOwnAsync(ct).ConfigureAwait(false)).Any(e => e.Kind == GitHubEntryKind.Folder && e.Name == folderName);

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var name = overwrite ? sourceItem.Name : await ResolveFileNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new GitHubPlaceholderFileItem(repository.FullName(GitHubPath.Combine(path, name)), name);
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var name = overwrite ? sourceItem.Name : await ResolveFolderNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new GitHubPlaceholderFolderItem(repository, GitHubPath.Combine(path, name));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The folder's entries; none when the folder is gone (nothing is contained in it).</summary>
    private async Task<IReadOnlyList<GitHubEntry>> ListOwnAsync(CancellationToken ct)
    {
        try
        {
            return await repository.ListAsync(path, ct: ct).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private async Task<string> ResolveFileNameAsync(string name, CancellationToken ct)
    {
        var taken = (await ListOwnAsync(ct).ConfigureAwait(false)).Select(e => e.Name).ToHashSet();

        if (!taken.Contains(name))
            return name;

        var baseName = System.IO.Path.GetFileNameWithoutExtension(name);
        var extension = System.IO.Path.GetExtension(name);

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} ({index}){extension}";

            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private async Task<string> ResolveFolderNameAsync(string name, CancellationToken ct)
    {
        var taken = (await ListOwnAsync(ct).ConfigureAwait(false)).Select(e => e.Name).ToHashSet();

        if (!taken.Contains(name))
            return name;

        for (var index = 2; ; index++)
        {
            var candidate = $"{name} ({index})";

            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private async Task<IFileItem> PutAsync(string file, byte[] content, string message, CancellationToken ct)
    {
        var head = await repository.GetHeadAsync(refresh: true, ct).ConfigureAwait(false);

        if ((await repository.GetAttributesAsync(head, ct).ConfigureAwait(false)).IsLfs(file))
            throw new NotSupportedException($"'{file}' is stored in Git LFS, which FileVault does not write.");

        await repository.CommitAsync([GitHubChange.Put(file, content)], message, null, ct).ConfigureAwait(false);

        return await FindFileAsync(file, ct).ConfigureAwait(false);
    }

    private async Task<IFileItem> FindFileAsync(string file, CancellationToken ct)
    {
        var head = await repository.GetHeadAsync(ct: ct).ConfigureAwait(false);

        return await repository.FindAsync(file, head, ct).ConfigureAwait(false) is { Kind: GitHubEntryKind.File } entry
            ? new GitHubFileItem(repository, entry, head.Date)
            : new GitHubPlaceholderFileItem(repository.FullName(file), file[(file.LastIndexOf('/') + 1)..]);
    }

    private static async Task<byte[]> ReadAllAsync(IFileItem source, IProgress<double> progress, CancellationToken ct)
    {
        if (source.Size > GitHubRepository.MaxBlobBytes)
            throw new NotSupportedException($"'{source.Name}' is over 100 MB; GitHub takes such files through Git LFS only.");

        var (stream, length) = await source.OpenReadAsync(ct).ConfigureAwait(false);

        await using (stream)
        {
            using var buffer = new MemoryStream();
            await FileOperationsHelper.CopyAsync(stream, length, buffer, progress, ct).ConfigureAwait(false);

            if (buffer.Length > GitHubRepository.MaxBlobBytes)
                throw new NotSupportedException($"'{source.Name}' is over 100 MB; GitHub takes such files through Git LFS only.");

            return buffer.ToArray();
        }
    }

    /// <summary>The path of an item of this repository; anything else cannot be renamed or deleted here.</summary>
    private string OwnPath(IFileProviderItem item) => item switch
    {
        GitHubFileItem file when ReferenceEquals(file.Repository, repository) => file.Path,
        GitHubFolderItem { Path.Length: > 0 } folder when ReferenceEquals(folder.Repository, repository) => folder.Path,
        _ => throw new ArgumentException($"'{item.FullName}' is not an item of {repository.FullName("")}.", nameof(item))
    };

    /// <summary>A destination must be in this repository and branch; any other FullName is refused.</summary>
    private string ParseDestination(string destinationPath)
    {
        if (!GitHubPath.TryParse(destinationPath, out var owner, out var name, out var branch, out var relative)
            || relative.Length == 0
            || !owner.Equals(repository.Owner, StringComparison.OrdinalIgnoreCase)
            || !name.Equals(repository.Name, StringComparison.OrdinalIgnoreCase)
            || branch != repository.Branch)
            throw new ArgumentException($"'{destinationPath}' is not a path in {repository.FullName("")}.",
                nameof(destinationPath));

        return relative;
    }

    private static void EnsureNotInside(string source, string destination)
    {
        if (destination.StartsWith(source + "/", StringComparison.Ordinal))
            throw new IOException($"Cannot put '{source}' inside itself.");
    }
}
