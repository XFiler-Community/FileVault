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
        var items = await Task.Run(() => client.ListDirectory(remotePath), ct).ConfigureAwait(false);
        foreach (var item in items)
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
            await Task.Run(() => client.CreateDirectory(path), ct).ConfigureAwait(false);
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
            await Task.Run(() =>
            {
                using var stream = client.OpenWrite(path);
            }, ct).ConfigureAwait(false);
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
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (item is IFileItem)
                    await Task.Run(() => client.DeleteFile(item.FullName), ct).ConfigureAwait(false);
                else
                    await Task.Run(() => client.DeleteDirectory(item.FullName), ct).ConfigureAwait(false);
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
            await Task.Run(() => client.RenameFile(item.FullName, newPath), ct).ConfigureAwait(false);
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
                await Task.Run(() =>
                {
                    using var dst = client.OpenWrite(destinationPath);
                    src.CopyTo(dst);
                }, ct).ConfigureAwait(false);
            }
            progress.Report(1.0);
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
            if (sourceItem is SftpFileItem sftpFile && ReferenceEquals(sftpFile, sourceItem))
            {
                await Task.Run(() => client.RenameFile(sftpFile.FullName, destinationPath), ct).ConfigureAwait(false);
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
            await Task.Run(() => client.RenameFile(sftpFolder.FullName, destinationPath), ct).ConfigureAwait(false);
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
        => await Task.Run(() => client.Exists(CombinePath(remotePath, fileName)), ct).ConfigureAwait(false);

    public async Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default)
        => await Task.Run(() => client.Exists(CombinePath(remotePath, folderName)), ct).ConfigureAwait(false);

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

    private static string CombinePath(string parent, string child)
        => parent.TrimEnd('/') + "/" + child;

    private static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }
}
