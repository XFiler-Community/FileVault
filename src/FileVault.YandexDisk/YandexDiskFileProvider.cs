using Egorozh.YandexDisk.Client;
using Egorozh.YandexDisk.Client.Clients;
using Egorozh.YandexDisk.Client.Protocol;
using FileVault.Core;

namespace FileVault.YandexDisk;

public sealed class YandexDiskFileProvider(IDiskApi api, string remotePath) : IFileProvider
{
    private const int PageSize = 20;

    public string Name => remotePath.TrimEnd('/').Split('/').Last() is { Length: > 0 } n ? n : "disk";
    public string FullName => remotePath;

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var offset = 0;
        while (true)
        {
            var resource = await api.MetaInfo.GetInfoAsync(new ResourceRequest
            {
                Path = remotePath,
                Limit = PageSize,
                Offset = offset
            }, ct).ConfigureAwait(false);

            var items = resource.Embedded?.Items;
            if (items is null || items.Count == 0)
                yield break;

            foreach (var item in items)
            {
                if (!filter.ShowHiddenItems && item.Name.StartsWith('.'))
                    continue;

                if (item.Type == ResourceType.Dir)
                    yield return new YandexDiskFolderItem(item, api);
                else if (item.Type == ResourceType.File)
                    yield return new YandexDiskFileItem(item, api);
            }

            offset += items.Count;
            if (items.Count < PageSize)
                yield break;
        }
    }

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var resolvedName = await ResolveFolderNameAsync(name, ct).ConfigureAwait(false);
            var path = CombinePath(remotePath, resolvedName);
            await api.Commands.CreateDictionaryAsync(path, ct).ConfigureAwait(false);
            IFolderItem result = new YandexDiskPlaceholderFolderItem(path, api);
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
            var link = await api.Files.GetUploadLinkAsync(path, overwrite: true, ct).ConfigureAwait(false);
            await api.Files.UploadAsync(link, new MemoryStream(), ct).ConfigureAwait(false);
            IFileItem result = new YandexDiskPlaceholderFileItem(path);
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
                await api.Commands.DeleteAndWaitAsync(
                    new DeleteFileRequest { Path = item.FullName, Permanently = !toRecycleBin }, ct)
                    .ConfigureAwait(false);
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
            await api.Commands.MoveAndWaitAsync(
                new MoveFileRequest { From = item.FullName, Path = newPath, Overwrite = false }, ct)
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
            if (sourceItem is YandexDiskFileItem ydItem)
            {
                await api.Commands.CopyAndWaitAsync(
                    new CopyFileRequest { From = ydItem.FullName, Path = destinationPath, Overwrite = true }, ct)
                    .ConfigureAwait(false);
                progress.Report(1.0);
            }
            else
            {
                (var src, long len) = await sourceItem.OpenReadAsync(ct).ConfigureAwait(false);
                await using (src)
                {
                    var link = await api.Files.GetUploadLinkAsync(destinationPath, overwrite: true, ct).ConfigureAwait(false);
                    await api.Files.UploadWithProgressAsync(link, src, len, v => progress.Report(v), ct).ConfigureAwait(false);
                }
            }
            IFileItem result = new YandexDiskPlaceholderFileItem(destinationPath);
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
            if (sourceItem is YandexDiskFileItem ydItem)
            {
                await api.Commands.MoveAndWaitAsync(
                    new MoveFileRequest { From = ydItem.FullName, Path = destinationPath, Overwrite = true }, ct)
                    .ConfigureAwait(false);
                progress.Report(1.0);
            }
            else
            {
                var copyResult = await CopyFileInAsync(sourceItem, destinationPath, progress, ct).ConfigureAwait(false);
                if (!copyResult.IsSuccess)
                    return copyResult;
                await sourceItem.DeleteAsync(ct).ConfigureAwait(false);
            }
            IFileItem result = new YandexDiskPlaceholderFileItem(destinationPath);
            return FileOperationResult<IFileItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        if (sourceItem is not YandexDiskFolderItem ydFolder)
            return FileOperationResult<IFolderItem?>.Success(null);

        try
        {
            await api.Commands.CopyAndWaitAsync(
                new CopyFileRequest { From = ydFolder.FullName, Path = destinationPath, Overwrite = true }, ct)
                .ConfigureAwait(false);
            progress.Report(1.0);
            IFolderItem? result = new YandexDiskPlaceholderFolderItem(destinationPath, api);
            return FileOperationResult<IFolderItem?>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        if (sourceItem is not YandexDiskFolderItem ydFolder)
            return FileOperationResult<IFolderItem?>.Success(null);

        try
        {
            await api.Commands.MoveAndWaitAsync(
                new MoveFileRequest { From = ydFolder.FullName, Path = destinationPath, Overwrite = true }, ct)
                .ConfigureAwait(false);
            progress.Report(1.0);
            IFolderItem? result = new YandexDiskPlaceholderFolderItem(destinationPath, api);
            return FileOperationResult<IFolderItem?>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            var path = CombinePath(remotePath, fileName);
            var resource = await api.MetaInfo.GetInfoAsync(new ResourceRequest { Path = path }, ct).ConfigureAwait(false);
            return resource.Type == ResourceType.File;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default)
    {
        try
        {
            var path = CombinePath(remotePath, folderName);
            var resource = await api.MetaInfo.GetInfoAsync(new ResourceRequest { Path = path }, ct).ConfigureAwait(false);
            return resource.Type == ResourceType.Dir;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        if (overwrite)
            return new YandexDiskPlaceholderFileItem(CombinePath(remotePath, sourceItem.Name));

        var name = await FindUniqueFileNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new YandexDiskPlaceholderFileItem(CombinePath(remotePath, name));
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        if (overwrite)
            return new YandexDiskPlaceholderFolderItem(CombinePath(remotePath, sourceItem.Name), api);

        var name = await FindUniqueFolderNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new YandexDiskPlaceholderFolderItem(CombinePath(remotePath, name), api);
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
        return lastSlash <= 0 ? "disk:/" : trimmed[..lastSlash];
    }
}
