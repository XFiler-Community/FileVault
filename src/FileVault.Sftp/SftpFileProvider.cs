using FileVault.Core;
using Renci.SshNet;

namespace FileVault.Sftp;

public sealed class SftpFileProvider(SftpClient client, string remotePath) : IFileProvider
{
    public string Name => Path.GetFileName(remotePath.TrimEnd('/')) is { Length: > 0 } n ? n : remotePath;
    public string FullName => $"sftp://{client.ConnectionInfo.Host}{remotePath}";

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in client.ListDirectoryAsync(remotePath, ct).ConfigureAwait(false))
        {
            if (item.Name is "." or "..")
                continue;
            if (!filter.ShowHiddenItems && item.Name.StartsWith('.'))
                continue;

            if (item.IsDirectory)
                yield return new SftpFolderItem(item, client);
            else if (item.IsRegularFile)
                yield return new SftpFileItem(item, client);
        }
    }

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var resolvedName = await ResolveFolderNameAsync(name, ct).ConfigureAwait(false);
            var path = CombinePath(remotePath, resolvedName);
            await client.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
            IFolderItem result = new SftpPlaceholderFolderItem(path, client);
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
            var stream = await client.OpenAsync(path, FileMode.OpenOrCreate, FileAccess.Write, ct).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            IFileItem result = new SftpPlaceholderFileItem(path);
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
                    await client.DeleteFileAsync(item.FullName, ct).ConfigureAwait(false);
                else
                    await client.DeleteDirectoryAsync(item.FullName, ct).ConfigureAwait(false);
            }
            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Success(items);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName, CancellationToken ct = default)
    {
        try
        {
            var newPath = CombinePath(GetParentPath(item.FullName), newName);
            await client.RenameFileAsync(item.FullName, newPath, ct).ConfigureAwait(false);
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
            (var src, long len) = await sourceItem.OpenReadAsync(ct).ConfigureAwait(false);
            await using (src)
            {
                await UploadAsync(src, len, destinationPath, progress, ct).ConfigureAwait(false);
            }
            IFileItem result = new SftpPlaceholderFileItem(destinationPath);
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
            if (sourceItem is SftpFileItem sftpFile && ReferenceEquals(sftpFile.Client, client))
            {
                if (sftpFile.FullName != destinationPath)
                    await client.RenameFileAsync(sftpFile.FullName, destinationPath, ct).ConfigureAwait(false);
                progress.Report(1.0);
            }
            else
            {
                var copyResult = await CopyFileInAsync(sourceItem, destinationPath, progress, ct).ConfigureAwait(false);
                if (!copyResult.IsSuccess)
                    return copyResult;
                await sourceItem.DeleteAsync(ct).ConfigureAwait(false);
            }
            IFileItem result = new SftpPlaceholderFileItem(destinationPath);
            return FileOperationResult<IFileItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
        => Task.FromResult(FileOperationResult<IFolderItem?>.Success(null));

    public async Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        if (sourceItem is not SftpFolderItem sftpFolder || !ReferenceEquals(sftpFolder.Client, client))
            return FileOperationResult<IFolderItem?>.Success(null);

        try
        {
            await client.RenameFileAsync(sftpFolder.FullName, destinationPath, ct).ConfigureAwait(false);
            progress.Report(1.0);
            IFolderItem? result = new SftpPlaceholderFolderItem(destinationPath, client);
            return FileOperationResult<IFolderItem?>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default)
        => await client.ExistsAsync(CombinePath(remotePath, fileName), ct).ConfigureAwait(false);

    public async Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default)
        => await client.ExistsAsync(CombinePath(remotePath, folderName), ct).ConfigureAwait(false);

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        if (overwrite)
            return new SftpPlaceholderFileItem(CombinePath(remotePath, sourceItem.Name));

        var name = await FindUniqueFileNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new SftpPlaceholderFileItem(CombinePath(remotePath, name));
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        if (overwrite)
            return new SftpPlaceholderFolderItem(CombinePath(remotePath, sourceItem.Name), client);

        var name = await FindUniqueFolderNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new SftpPlaceholderFolderItem(CombinePath(remotePath, name), client);
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

    // Upload to a temp file next to the destination and swap it in only when complete:
    // a cancelled or failed upload leaves neither a truncated file nor a damaged original (Replace).
    private async Task UploadAsync(Stream src, long totalBytes, string destinationPath,
        IProgress<double> progress, CancellationToken ct)
    {
        var tempPath = GetPartialPath(destinationPath);
        try
        {
            var dst = await client.OpenAsync(tempPath, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false);
            await using (dst)
            {
                await FileOperationsHelper.CopyAsync(src, totalBytes, dst, progress, ct).ConfigureAwait(false);
            }
            await ReplaceAsync(tempPath, destinationPath, ct).ConfigureAwait(false);
        }
        catch
        {
            await TryDeleteFileAsync(tempPath).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReplaceAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (!await client.ExistsAsync(destinationPath, ct).ConfigureAwait(false))
        {
            await client.RenameFileAsync(sourcePath, destinationPath, ct).ConfigureAwait(false);
            return;
        }

        // Plain SFTP rename refuses to overwrite; posix-rename (OpenSSH extension) replaces atomically.
        try
        {
            await Task.Run(() => client.RenameFile(sourcePath, destinationPath, isPosix: true), ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            await client.DeleteFileAsync(destinationPath, ct).ConfigureAwait(false);
            await client.RenameFileAsync(sourcePath, destinationPath, ct).ConfigureAwait(false);
        }
    }

    private async Task TryDeleteFileAsync(string path)
    {
        // The operation token is usually already cancelled here; cleanup gets its own short deadline.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await client.DeleteFileAsync(path, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // The original error is what the caller needs; a leftover temp file is the lesser problem.
        }
    }

    private static string GetPartialPath(string destinationPath)
    {
        var name = GetFileName(destinationPath);
        // Keep the temp name within the common 255-char file name limit.
        var prefix = name.Length <= 200 ? name : string.Empty;
        return CombinePath(GetParentPath(destinationPath), $"{prefix}.filevault-partial-{Guid.NewGuid():N}");
    }

    private static string GetFileName(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
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
