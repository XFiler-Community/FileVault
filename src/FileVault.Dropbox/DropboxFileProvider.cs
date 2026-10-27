using FileVault.Core;

namespace FileVault.Dropbox;

/// <summary>
/// A folder of a Dropbox account; <paramref name="path"/> empty is the root. Names are compared the way Dropbox does,
/// case-insensitively. Copies and moves between items of the same <see cref="DropboxClient"/> run on Dropbox;
/// uploads commit atomically, so a cancelled copy leaves nothing behind. <c>toRecycleBin: true</c> deletes with
/// <c>delete_v2</c> (restorable for the account's retention period); <c>toRecycleBin: false</c> asks for
/// <c>permanently_delete</c>, which only Dropbox Business apps may use — elsewhere the delete fails.
/// </summary>
public sealed class DropboxFileProvider(DropboxClient client, string path = "") : ISearchableFileProvider
{
    public DropboxClient Client => client;
    public string Path => path;

    public string Name => path.Length == 0 ? "Dropbox" : DropboxPath.GetName(path);
    public string FullName => DropboxPath.Format(path);

    /// <summary>Folders stream as their pages arrive; files follow once the listing is complete.</summary>
    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = new List<DropboxEntry>();

        await foreach (var entry in client.ListAsync(path, ct).ConfigureAwait(false))
        {
            if (!filter.ShowHiddenItems && entry.Name.StartsWith('.'))
                continue;

            if (entry.Kind == DropboxEntryKind.Folder)
                yield return new DropboxFolderItem(client, entry);
            else
                files.Add(entry);
        }

        foreach (var file in files)
            yield return new DropboxFileItem(client, file);
    }

    public async IAsyncEnumerable<IFileProviderItem> SearchAsync(string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            yield break;

        await foreach (var entry in client.SearchAsync(path, query.Trim(), ct).ConfigureAwait(false))
            yield return ToItem(entry);
    }

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            DropboxPath.EnsureValidName(name);
            var resolved = await ResolveNameAsync(name, isFile: false, ct).ConfigureAwait(false);
            var entry = await client.CreateFolderAsync(DropboxPath.Combine(path, resolved), ct).ConfigureAwait(false);

            return FileOperationResult<IFolderItem>.Success(new DropboxFolderItem(client, entry));
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
            DropboxPath.EnsureValidName(name);
            var resolved = await ResolveNameAsync(name, isFile: true, ct).ConfigureAwait(false);

            using var empty = new MemoryStream();
            var entry = await client.UploadAsync(DropboxPath.Combine(path, resolved), empty, 0, overwrite: false, null, ct)
                .ConfigureAwait(false);

            return FileOperationResult<IFileItem>.Success(new DropboxFileItem(client, entry));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    /// <summary>
    /// <paramref name="toRecycleBin"/> true: <c>delete_v2</c>, restorable from Dropbox's deleted files. False:
    /// <c>permanently_delete</c>, available to Dropbox Business apps only; when Dropbox refuses it the result is a
    /// failure — it never falls back to a restorable delete. Items are deleted one by one and the first failure
    /// stops the rest.
    /// </summary>
    public async Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default)
    {
        try
        {
            var paths = items.Select(OwnPath).ToList();

            foreach (var item in paths)
            {
                ct.ThrowIfCancellationRequested();

                if (toRecycleBin)
                    await client.DeleteAsync(item, ct).ConfigureAwait(false);
                else
                    await client.PermanentlyDeleteAsync(item, ct).ConfigureAwait(false);
            }

            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Success(items);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Failure(ex);
        }
    }

    /// <summary>
    /// A move to a sibling. Dropbox does not rename by case alone, so a case-only rename goes through a temporary
    /// name in the same folder.
    /// </summary>
    public async Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName,
        CancellationToken ct = default)
    {
        try
        {
            DropboxPath.EnsureValidName(newName);
            var from = OwnPath(item);
            var to = DropboxPath.Combine(DropboxPath.Parent(from), newName);

            await MoveWithinAsync(from, to, ct).ConfigureAwait(false);
            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }

    /// <summary>
    /// Same account: <c>copy_v2</c>. Elsewhere: the source is read and uploaded. An existing file at the destination
    /// is replaced (the caller asked for that name through <see cref="ResolveDestinationFileAsync"/>); the old
    /// content stays in the file's version history, or — for a server-side copy, which cannot overwrite — in the
    /// deleted files.
    /// </summary>
    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var destination = ParseDestination(destinationPath);

            if (sourceItem is DropboxFileItem own && ReferenceEquals(own.Client, client))
            {
                var entry = await ReplacingFileAsync(destination,
                    () => client.CopyAsync(own.Path, destination, ct), ct).ConfigureAwait(false);
                progress.Report(1);

                return FileOperationResult<IFileItem>.Success(new DropboxFileItem(client, entry));
            }

            (var source, long length) = await sourceItem.OpenReadAsync(ct).ConfigureAwait(false);

            await using (source)
            {
                var entry = await client.UploadAsync(destination, source, length, overwrite: true, progress, ct)
                    .ConfigureAwait(false);

                return FileOperationResult<IFileItem>.Success(new DropboxFileItem(client, entry));
            }
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

            if (sourceItem is DropboxFileItem own && ReferenceEquals(own.Client, client))
            {
                if (own.Path == destination)
                {
                    progress.Report(1);
                    return FileOperationResult<IFileItem>.Success(own);
                }

                var entry = DropboxPath.AreSame(own.Path, destination)
                    ? await MoveWithinAsync(own.Path, destination, ct).ConfigureAwait(false)
                    : await ReplacingFileAsync(destination, () => client.MoveAsync(own.Path, destination, ct), ct)
                        .ConfigureAwait(false);
                progress.Report(1);

                return FileOperationResult<IFileItem>.Success(new DropboxFileItem(client, entry));
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

    /// <summary>
    /// Same account: <c>copy_v2</c> of the whole folder (a batch job over 10 000 entries). When the destination
    /// folder already exists Dropbox cannot merge into it: <c>Success(null)</c> lets the caller copy item by item.
    /// </summary>
    public async Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            if (sourceItem is not DropboxFolderItem own || !ReferenceEquals(own.Client, client))
                return FileOperationResult<IFolderItem?>.Success(null);

            var destination = ParseDestination(destinationPath);
            EnsureNotInside(own.Path, destination);

            var entry = await client.CopyAsync(own.Path, destination, ct).ConfigureAwait(false);
            progress.Report(1);

            return FileOperationResult<IFolderItem?>.Success(new DropboxFolderItem(client, entry));
        }
        catch (DropboxApiException e) when (e.HasPath("to/conflict"))
        {
            return FileOperationResult<IFolderItem?>.Success(null);
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
            if (sourceItem is not DropboxFolderItem own || !ReferenceEquals(own.Client, client))
                return FileOperationResult<IFolderItem?>.Success(null);

            var destination = ParseDestination(destinationPath);

            if (own.Path == destination)
            {
                progress.Report(1);
                return FileOperationResult<IFolderItem?>.Success(own);
            }

            var entry = DropboxPath.AreSame(own.Path, destination)
                ? await MoveWithinAsync(own.Path, destination, ct).ConfigureAwait(false)
                : await MoveFolderAsync(own.Path, destination, ct).ConfigureAwait(false);

            progress.Report(1);
            return FileOperationResult<IFolderItem?>.Success(new DropboxFolderItem(client, entry));
        }
        catch (DropboxApiException e) when (e.HasPath("to/conflict"))
        {
            return FileOperationResult<IFolderItem?>.Success(null);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default) =>
        ContainsAsync(fileName, DropboxEntryKind.File, ct);

    public Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default) =>
        ContainsAsync(folderName, DropboxEntryKind.Folder, ct);

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var name = overwrite ? sourceItem.Name : await ResolveNameAsync(sourceItem.Name, isFile: true, ct).ConfigureAwait(false);
        return new DropboxPlaceholderFileItem(DropboxPath.Format(DropboxPath.Combine(path, name)), name);
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var name = overwrite ? sourceItem.Name : await ResolveNameAsync(sourceItem.Name, isFile: false, ct).ConfigureAwait(false);
        return new DropboxPlaceholderFolderItem(client, DropboxPath.Combine(path, name));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private IFileProviderItem ToItem(DropboxEntry entry) => entry.Kind == DropboxEntryKind.Folder
        ? new DropboxFolderItem(client, entry)
        : new DropboxFileItem(client, entry);

    /// <summary>Only "not found" is false; any other failure throws, so a caller never reuses a taken name.</summary>
    private async Task<bool> ContainsAsync(string name, DropboxEntryKind kind, CancellationToken ct)
    {
        if (!DropboxPath.IsValidName(name))
            return false;

        return await client.GetEntryAsync(DropboxPath.Combine(path, name), ct).ConfigureAwait(false) is { } entry
               && entry.Kind == kind;
    }

    /// <summary>Taken names of the folder, case-insensitive like Dropbox; none when the folder does not exist yet.</summary>
    private async Task<HashSet<string>> ListNamesAsync(CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await foreach (var entry in client.ListAsync(path, ct).ConfigureAwait(false))
                names.Add(entry.Name);
        }
        catch (DropboxApiException e) when (e.IsNotFound)
        {
        }

        return names;
    }

    // A file and a folder cannot share a name in Dropbox, so both kinds count as taken
    private async Task<string> ResolveNameAsync(string name, bool isFile, CancellationToken ct)
    {
        var taken = await ListNamesAsync(ct).ConfigureAwait(false);

        if (!taken.Contains(name))
            return name;

        var baseName = isFile ? System.IO.Path.GetFileNameWithoutExtension(name) : name;
        var extension = isFile ? System.IO.Path.GetExtension(name) : "";

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} ({index}){extension}";

            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// <c>copy_v2</c> and <c>move_v2</c> never overwrite: when a file is in the way, it goes to the deleted files
    /// (restorable) and the call is made once more.
    /// </summary>
    private async Task<DropboxEntry> ReplacingFileAsync(string destination, Func<Task<DropboxEntry>> relocate,
        CancellationToken ct)
    {
        try
        {
            return await relocate().ConfigureAwait(false);
        }
        catch (DropboxApiException e) when (e.HasPath("to/conflict/file"))
        {
            await client.DeleteAsync(destination, ct).ConfigureAwait(false);
            return await relocate().ConfigureAwait(false);
        }
    }

    private async Task<DropboxEntry> MoveFolderAsync(string from, string to, CancellationToken ct)
    {
        EnsureNotInside(from, to);
        return await client.MoveAsync(from, to, ct).ConfigureAwait(false);
    }

    /// <summary>A move that may change only the case of the name: Dropbox needs a stop at another name for that.</summary>
    private async Task<DropboxEntry> MoveWithinAsync(string from, string to, CancellationToken ct)
    {
        if (!DropboxPath.AreSame(from, to))
            return await client.MoveAsync(from, to, ct).ConfigureAwait(false);

        if (from == to)
            return await client.GetEntryAsync(from, ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"'{from}' does not exist in Dropbox.");

        var temporary = DropboxPath.Combine(DropboxPath.Parent(from),
            $"{DropboxPath.GetName(from)}.filevault-rename-{Guid.NewGuid():N}");

        await client.MoveAsync(from, temporary, ct).ConfigureAwait(false);

        try
        {
            return await client.MoveAsync(temporary, to, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await client.MoveAsync(temporary, from, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The path of an item of this account; anything else cannot be renamed or deleted here.</summary>
    private string OwnPath(IFileProviderItem item) => item switch
    {
        DropboxFileItem file when ReferenceEquals(file.Client, client) => file.Path,
        DropboxFolderItem folder when ReferenceEquals(folder.Client, client) => folder.Path,
        _ => throw new ArgumentException($"'{item.FullName}' is not an item of this Dropbox account.", nameof(item))
    };

    /// <summary>A destination must be a Dropbox path below the root; any other FullName is refused.</summary>
    private static string ParseDestination(string destinationPath)
    {
        if (!DropboxPath.TryParse(destinationPath, out var destination) || destination.Length == 0)
            throw new ArgumentException($"'{destinationPath}' is not a Dropbox path.", nameof(destinationPath));

        return destination;
    }

    private static void EnsureNotInside(string source, string destination)
    {
        if (destination.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Cannot put '{source}' inside itself.");
    }
}
