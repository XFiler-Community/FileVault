using FileVault.Core;
using FluentFTP;

namespace FileVault.Ftp;

public sealed class FtpFileProvider(AsyncFtpClient client, string remotePath) : IFileProvider, IFolderSizeProvider
{
    public string Name => Path.GetFileName(remotePath.TrimEnd('/')) is { Length: > 0 } n ? n : remotePath;
    public string FullName => $"ftp://{client.Host}{remotePath}";

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var items = await client.GetListing(remotePath, token: ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            if (filter.ShowHiddenItems is false && item.Name.StartsWith('.'))
                continue;

            if (item.Type == FtpObjectType.Directory)
                yield return new FtpFolderItem(item, client);
            else if (item.Type == FtpObjectType.File)
                yield return new FtpFileItem(item, client);
        }
    }

    /// <summary>Every folder below is listed, one at a time: one FTP connection serves one command at a time.</summary>
    public IAsyncEnumerable<FolderSizes> MeasureFoldersAsync(CancellationToken ct = default) =>
        FolderSizeWalker.MeasureAsync(this, maxParallelism: 1, ct: ct);

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var resolvedName = await ResolveFolderNameAsync(name, ct).ConfigureAwait(false);
            var path = CombinePath(remotePath, resolvedName);
            await client.CreateDirectory(path, token: ct).ConfigureAwait(false);
            var listing = await client.GetObjectInfo(path, token: ct).ConfigureAwait(false);
            IFolderItem result = listing is not null
                ? new FtpFolderItem(listing, client)
                : new FtpPlaceholderFolderItem(path, client);
            return FileOperationResult<IFolderItem>.Success(result);
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
            var path = CombinePath(remotePath, name);
            using var empty = new MemoryStream();
            await client.UploadStream(empty, path, FtpRemoteExists.Overwrite, token: ct).ConfigureAwait(false);
            IFileItem result = new FtpPlaceholderFileItem(path);
            return FileOperationResult<IFileItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default)
    {
        try
        {
            if (toRecycleBin)
                throw new NotSupportedException("Recycle bin is not available for this location.");

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (item is IFileItem)
                    await client.DeleteFile(item.FullName, ct).ConfigureAwait(false);
                else
                    await client.DeleteDirectory(item.FullName, token: ct).ConfigureAwait(false);
            }
            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Success(items);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName, CancellationToken ct = default) =>
        (await RenameItemAsync(item, newName, ct).ConfigureAwait(false)).Map(_ => true);

    /// <summary>
    /// The renamed item is read back with MLST when the server has it; otherwise it keeps the item's size and date,
    /// which a rename does not change.
    /// </summary>
    public async Task<FileOperationResult<IFileProviderItem>> RenameItemAsync(IFileProviderItem item, string newName,
        CancellationToken ct = default)
    {
        try
        {
            var dir = GetParentPath(item.FullName);
            var newPath = CombinePath(dir, newName);
            var isFile = item is IFileItem;
            if (isFile)
                await client.MoveFile(item.FullName, newPath, FtpRemoteExists.Overwrite, ct).ConfigureAwait(false);
            else
                await client.MoveDirectory(item.FullName, newPath, FtpRemoteExists.Overwrite, ct).ConfigureAwait(false);

            var listing = await TryGetObjectInfoAsync(newPath, ct).ConfigureAwait(false)
                ?? new FtpListItem
                {
                    Name = newName,
                    FullName = newPath,
                    Type = isFile ? FtpObjectType.File : FtpObjectType.Directory,
                    Size = item.Size ?? 0,
                    Modified = item.ChangedDate.LocalDateTime
                };

            IFileProviderItem renamed = isFile ? new FtpFileItem(listing, client) : new FtpFolderItem(listing, client);
            return FileOperationResult<IFileProviderItem>.Success(renamed);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileProviderItem>.Failure(ex);
        }
    }

    // The rename has happened: a failed read-back must not report it as failed.
    private async Task<FtpListItem?> TryGetObjectInfoAsync(string path, CancellationToken ct)
    {
        try
        {
            return await client.GetObjectInfo(path, token: ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            (var src, long len) = await sourceItem.OpenReadAsync(ct).ConfigureAwait(false);
            await using (src)
            {
                await client.UploadStream(src, destinationPath, FtpRemoteExists.Overwrite, token: ct).ConfigureAwait(false);
            }
            progress.Report(1.0);
            IFileItem result = new FtpPlaceholderFileItem(destinationPath);
            return FileOperationResult<IFileItem>.Success(result);
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
            // Same type is not enough: an item from another server must go through copy + delete.
            // Moving a file onto itself must be a no-op, not copy + delete of the only copy.
            if (sourceItem is FtpFileItem ftpFile && ReferenceEquals(ftpFile.Client, client))
            {
                if (ftpFile.FullName != destinationPath)
                    await client.MoveFile(ftpFile.FullName, destinationPath, FtpRemoteExists.Overwrite, ct).ConfigureAwait(false);
                progress.Report(1.0);
            }
            else
            {
                var copyResult = await CopyFileInAsync(sourceItem, destinationPath, progress, ct).ConfigureAwait(false);
                if (!copyResult.IsSuccess)
                    return copyResult;
                await sourceItem.DeleteAsync(ct).ConfigureAwait(false);
            }
            IFileItem result = new FtpPlaceholderFileItem(destinationPath);
            return FileOperationResult<IFileItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        // FTP протокол не поддерживает нативное копирование директорий — сигнализируем оркестратору делать рекурсию.
        return Task.FromResult(FileOperationResult<IFolderItem?>.Success(null));
    }

    public async Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        if (sourceItem is not FtpFolderItem ftpFolder || !ReferenceEquals(ftpFolder.Client, client))
            return FileOperationResult<IFolderItem?>.Success(null);

        try
        {
            await client.MoveDirectory(ftpFolder.FullName, destinationPath, FtpRemoteExists.Overwrite, ct).ConfigureAwait(false);
            progress.Report(1.0);
            IFolderItem? result = new FtpPlaceholderFolderItem(destinationPath, client);
            return FileOperationResult<IFolderItem?>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default)
        => await client.FileExists(CombinePath(remotePath, fileName), ct).ConfigureAwait(false);

    public async Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default)
        => await client.DirectoryExists(CombinePath(remotePath, folderName), ct).ConfigureAwait(false);

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        if (overwrite)
            return new FtpPlaceholderFileItem(CombinePath(remotePath, sourceItem.Name));

        var name = await FindUniqueFileNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new FtpPlaceholderFileItem(CombinePath(remotePath, name));
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        if (overwrite)
            return new FtpPlaceholderFolderItem(CombinePath(remotePath, sourceItem.Name), client);

        var name = await FindUniqueFolderNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new FtpPlaceholderFolderItem(CombinePath(remotePath, name), client);
    }

    private async Task<string> FindUniqueFileNameAsync(string fileName, CancellationToken ct)
    {
        if (!await ContainsFileAsync(fileName, ct).ConfigureAwait(false))
            return fileName;

        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var index = 2;
        string candidate;
        do
        {
            candidate = $"{baseName} ({index}){ext}";
            index++;
        }
        while (await ContainsFileAsync(candidate, ct).ConfigureAwait(false));
        return candidate;
    }

    private async Task<string> FindUniqueFolderNameAsync(string folderName, CancellationToken ct)
    {
        if (!await ContainsFolderAsync(folderName, ct).ConfigureAwait(false))
            return folderName;

        var index = 2;
        string candidate;
        do
        {
            candidate = $"{folderName} ({index})";
            index++;
        }
        while (await ContainsFolderAsync(candidate, ct).ConfigureAwait(false));
        return candidate;
    }

    private async Task<string> ResolveFolderNameAsync(string name, CancellationToken ct)
    {
        if (!await ContainsFolderAsync(name, ct).ConfigureAwait(false))
            return name;

        var index = 2;
        string candidate;
        do
        {
            candidate = $"{name} ({index})";
            index++;
        }
        while (await ContainsFolderAsync(candidate, ct).ConfigureAwait(false));
        return candidate;
    }

    private static string CombinePath(string parent, string child)
        => parent.TrimEnd('/') + "/" + child;

    private static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }
}
