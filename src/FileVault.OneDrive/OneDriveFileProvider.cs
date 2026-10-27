using FileVault.Core;

namespace FileVault.OneDrive;

/// <summary>
/// A folder of a OneDrive. Names are case-insensitive there, and a file and a folder cannot share one, so a name is
/// free only when no item of either kind has it.
/// </summary>
/// <remarks>
/// Uploads (<see cref="CopyFileInAsync"/> from another provider) replace a file of the same name: the caller chose the
/// name through <see cref="ResolveDestinationFileAsync"/>, where <c>overwrite: true</c> keeps a taken name to mean
/// "Replace". OneDrive commits an upload only when all bytes arrived, so a failed Replace keeps the old content.
/// New folders and new empty files never replace (<c>conflictBehavior=fail</c>).
/// </remarks>
public sealed class OneDriveFileProvider(OneDriveClient client, string folderId = OneDriveClient.RootId, string? name = null)
    : ISearchableFileProvider
{
    private const string Replace = "replace";
    private const string Fail = "fail";

    public OneDriveClient Client => client;
    public string FolderId => folderId;

    public string Name => name ?? (folderId == OneDriveClient.RootId ? "OneDrive" : folderId);
    public string FullName => OneDriveClient.FullNameOf(folderId);

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = new List<IFileProviderItem>();

        await foreach (var item in client.ListChildrenAsync(folderId, ct).ConfigureAwait(false))
        {
            if (item.RemoteItem is not null || (!filter.ShowHiddenItems && item.Name.StartsWith('.')))
                continue;

            if (item.Folder is not null)
                yield return new OneDriveFolderItem(client, item);
            else
                files.Add(new OneDriveFileItem(client, item));
        }

        foreach (var file in files)
            yield return file;
    }

    /// <summary>The file or folder at <paramref name="relativePath"/> below this folder (<c>a/b.md</c>); null when there is none.</summary>
    public async Task<IFileProviderItem?> FindAsync(string relativePath, CancellationToken ct = default) =>
        await client.TryGetByPathAsync(folderId, relativePath, ct).ConfigureAwait(false) is { } item
            ? client.ToItem(item)
            : null;

    public async IAsyncEnumerable<IFileProviderItem> SearchAsync(string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in client.SearchAsync(folderId, query, ct).ConfigureAwait(false))
            yield return client.ToItem(item);
    }

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var resolved = await FindFreeNameAsync(name, false, ct).ConfigureAwait(false);
            var created = await client.CreateFolderAsync(folderId, resolved, ct).ConfigureAwait(false);
            return FileOperationResult<IFolderItem>.Success(new OneDriveFolderItem(client, created));
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
            var resolved = await FindFreeNameAsync(name, true, ct).ConfigureAwait(false);
            using var empty = new MemoryStream();
            var created = await client.UploadAsync(folderId, resolved, empty, 0, Fail, null, ct).ConfigureAwait(false);
            return FileOperationResult<IFileItem>.Success(new OneDriveFileItem(client, created));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    /// <summary>
    /// <paramref name="toRecycleBin"/>: true — <c>DELETE</c>, which moves the items to the OneDrive recycle bin; false —
    /// <c>permanentDelete</c>. A drive that refuses permanent deletion returns Failure(<see cref="NotSupportedException"/>)
    /// on the first item, so nothing was deleted; the items are never moved to the recycle bin instead.
    /// </summary>
    public async Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default)
    {
        try
        {
            var ids = items.Select(OwnId).ToList();

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();

                if (toRecycleBin)
                    await client.MoveToRecycleBinAsync(id, ct).ConfigureAwait(false);
                else
                    await client.DeletePermanentlyAsync(id, ct).ConfigureAwait(false);
            }

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
            var id = OwnId(item);

            if (newName != item.Name)
                await client.UpdateAsync(id, newName, null, ct).ConfigureAwait(false);

            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }

    /// <summary>
    /// Within this drive — a server-side copy. When the name is taken (the caller chose "Replace"), the server copy
    /// cannot overwrite, so the content goes down and up again and replaces the file. From another provider — upload.
    /// </summary>
    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var (parentId, fileName) = ParseDestination(destinationPath);

            if (sourceItem is OneDriveFileItem own && ReferenceEquals(own.Client, client))
            {
                try
                {
                    var copied = await client.CopyAsync(own.Id, parentId, fileName, progress, ct).ConfigureAwait(false);
                    progress.Report(1);
                    return FileOperationResult<IFileItem>.Success(new OneDriveFileItem(client, copied));
                }
                catch (OneDriveApiException e) when (e.IsNameConflict)
                {
                }
            }

            var uploaded = await UploadFromAsync(sourceItem, parentId, fileName, progress, ct).ConfigureAwait(false);
            return FileOperationResult<IFileItem>.Success(new OneDriveFileItem(client, uploaded));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    /// <summary>
    /// Within this drive — one PATCH of parent and name. A taken name (the caller chose "Replace") cannot be moved over,
    /// so it becomes copy-and-replace, then the source goes to the recycle bin. From another provider — copy + delete.
    /// </summary>
    public async Task<FileOperationResult<IFileItem>> MoveFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var (parentId, fileName) = ParseDestination(destinationPath);

            if (sourceItem is OneDriveFileItem own && ReferenceEquals(own.Client, client))
            {
                var destinationParent = await client.ResolveIdAsync(parentId, ct).ConfigureAwait(false);

                if (own.ParentId == destinationParent && own.Name == fileName)
                {
                    progress.Report(1);
                    return FileOperationResult<IFileItem>.Success(own);
                }

                try
                {
                    var moved = await client.UpdateAsync(own.Id, own.Name == fileName ? null : fileName,
                        own.ParentId == destinationParent ? null : destinationParent, ct).ConfigureAwait(false);
                    progress.Report(1);
                    return FileOperationResult<IFileItem>.Success(new OneDriveFileItem(client, moved));
                }
                catch (OneDriveApiException e) when (e.IsNameConflict)
                {
                }
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
    /// Within this drive — one server-side copy of the whole tree. Into a taken name (merge chosen) or from another
    /// provider — Success(null): the orchestrator recurses.
    /// </summary>
    public async Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            if (sourceItem is not OneDriveFolderItem own || !ReferenceEquals(own.Client, client))
                return FileOperationResult<IFolderItem?>.Success(null);

            var (parentId, folderName) = ParseDestination(destinationPath);

            try
            {
                var copied = await client.CopyAsync(own.Id, parentId, folderName, progress, ct).ConfigureAwait(false);
                progress.Report(1);
                return FileOperationResult<IFolderItem?>.Success(new OneDriveFolderItem(client, copied));
            }
            catch (OneDriveApiException e) when (e.IsNameConflict)
            {
                return FileOperationResult<IFolderItem?>.Success(null);
            }
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
            if (sourceItem is not OneDriveFolderItem own || !ReferenceEquals(own.Client, client))
                return FileOperationResult<IFolderItem?>.Success(null);

            var (parentId, folderName) = ParseDestination(destinationPath);
            var destinationParent = await client.ResolveIdAsync(parentId, ct).ConfigureAwait(false);

            if (own.ParentId == destinationParent && own.Name == folderName)
            {
                progress.Report(1);
                return FileOperationResult<IFolderItem?>.Success(own);
            }

            try
            {
                var moved = await client.UpdateAsync(own.Id, own.Name == folderName ? null : folderName,
                    own.ParentId == destinationParent ? null : destinationParent, ct).ConfigureAwait(false);
                progress.Report(1);
                return FileOperationResult<IFolderItem?>.Success(new OneDriveFolderItem(client, moved));
            }
            catch (OneDriveApiException e) when (e.IsNameConflict)
            {
                return FileOperationResult<IFolderItem?>.Success(null);
            }
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default) =>
        await client.TryGetByPathAsync(folderId, fileName, ct).ConfigureAwait(false) is { Folder: null, Root: null };

    public async Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default) =>
        await client.TryGetByPathAsync(folderId, folderName, ct).ConfigureAwait(false) is { Folder: not null };

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var resolved = overwrite ? sourceItem.Name : await FindFreeNameAsync(sourceItem.Name, true, ct).ConfigureAwait(false);
        return new OneDrivePlaceholderFileItem(folderId, resolved);
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var resolved = overwrite ? sourceItem.Name : await FindFreeNameAsync(sourceItem.Name, false, ct).ConfigureAwait(false);
        return new OneDrivePlaceholderFolderItem(client, folderId, resolved);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="name"/>, or <c>name (2)</c>, <c>name (3)</c>… (files: before the extension) — the first that no
    /// item of this folder has. A failed lookup throws: answering "free" would make an upload replace a file.
    /// </summary>
    private async Task<string> FindFreeNameAsync(string name, bool isFile, CancellationToken ct)
    {
        if (await client.TryGetByPathAsync(folderId, name, ct).ConfigureAwait(false) is null)
            return name;

        var baseName = isFile ? Path.GetFileNameWithoutExtension(name) : name;
        var extension = isFile ? Path.GetExtension(name) : "";

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} ({index}){extension}";

            if (await client.TryGetByPathAsync(folderId, candidate, ct).ConfigureAwait(false) is null)
                return candidate;
        }
    }

    private async Task<GraphDriveItem> UploadFromAsync(IFileItem source, string parentId, string fileName,
        IProgress<double> progress, CancellationToken ct)
    {
        var (stream, _) = await source.OpenReadAsync(ct).ConfigureAwait(false);

        await using (stream)
        {
            if (stream.CanSeek)
                return await client.UploadAsync(parentId, fileName, stream, stream.Length - stream.Position, Replace,
                    progress, ct).ConfigureAwait(false);

            // An upload session needs the length up front and a way to resend a fragment: spool to a temp file
            await using var buffered = await TemporaryFileBuffer.CreateAsync(
                (destination, token) => stream.CopyToAsync(destination, token), ct).ConfigureAwait(false);

            return await client.UploadAsync(parentId, fileName, buffered, buffered.Length, Replace, progress, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The id of an item of this client; items of other providers or accounts cannot be changed here.</summary>
    private string OwnId(IFileProviderItem item) => item switch
    {
        OneDriveFileItem file when ReferenceEquals(file.Client, client) => file.Id,
        OneDriveFolderItem folder when ReferenceEquals(folder.Client, client) => folder.Id,
        _ => throw new ArgumentException($"'{item.FullName}' is not an item of this OneDrive account.", nameof(item))
    };

    /// <summary>
    /// <c>onedrive:&lt;parent-id&gt;/&lt;name&gt;</c>. A name alone (no scheme, no slash) means this folder; anything
    /// else is refused.
    /// </summary>
    private (string parentId, string name) ParseDestination(string destinationPath)
    {
        var hasScheme = destinationPath.StartsWith(OneDriveClient.Scheme, StringComparison.OrdinalIgnoreCase);
        var path = hasScheme ? destinationPath[OneDriveClient.Scheme.Length..] : destinationPath;
        var slash = path.IndexOf('/');

        if (slash < 0 && hasScheme)
            throw new ArgumentException($"'{destinationPath}' names an item, not a destination in a folder.",
                nameof(destinationPath));

        var (parentId, fileName) = slash < 0 ? (folderId, path) : (path[..slash], path[(slash + 1)..]);

        if (parentId.Length == 0 || fileName.Length == 0 || fileName.Contains('/'))
            throw new ArgumentException($"'{destinationPath}' is not a OneDrive destination.", nameof(destinationPath));

        return (parentId, fileName);
    }
}
