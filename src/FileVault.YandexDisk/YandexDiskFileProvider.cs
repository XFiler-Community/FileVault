using System.Runtime.CompilerServices;
using System.Net;
using XFiler.YandexDisk.Client;
using XFiler.YandexDisk.Client.Clients;
using XFiler.YandexDisk.Client.Protocol;
using FileVault.Core;

namespace FileVault.YandexDisk;

public sealed class YandexDiskFileProvider(IDiskApi api, string remotePath) : ISearchableFileProvider, IFolderSizeProvider, IParallelTransferProvider
{
    /// <inheritdoc />
    // Yandex throttles each upload link, not the account.
    public int MaxParallelTransfers => 4;

    private const int PageSize = 20;
    private const int SearchPageSize = 1000;
    private const int SizesPageSize = 5000;

    // One whole-disk count per account, reused for any folder while it is fresh
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDiskApi, DiskSizes> KnownSizes = new();

    // Bumped by every write, so a count that was running meanwhile is not kept
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDiskApi, StrongBox<int>> Writes = new();

    /// <summary>How long a finished count of the disk answers <see cref="MeasureFoldersAsync"/> for any of its folders.</summary>
    public static TimeSpan FolderSizesCacheDuration { get; set; } = TimeSpan.FromMinutes(2);

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

    /// <summary>
    /// Yandex Disk's REST API has no search by name, so this pages through its flat list of every file on the disk
    /// (<c>resources/files</c>, 1000 per request) and keeps those below this folder: a search costs as many requests
    /// as the whole disk has thousands of files, whichever folder it starts in. That list holds files only. A folder
    /// is found through the files inside it (at any depth) and read once for its metadata, so a folder with no file
    /// anywhere below it is not found. An empty <paramref name="query"/> finds nothing.
    /// </summary>
    public async IAsyncEnumerable<IFileProviderItem> SearchAsync(string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
            yield break;

        var prefix = SearchPrefix(remotePath);
        var foundFolders = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;

        while (true)
        {
            var page = await api.MetaInfo.GetFilesInfoAsync(
                new FilesResourceRequest { Limit = SearchPageSize, Offset = offset }, ct).ConfigureAwait(false);

            var files = page.Items;
            if (files.Count == 0)
                yield break;

            foreach (var file in files)
            {
                if (!file.Path.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var segments = file.Path[prefix.Length..].Split('/');
                var folderPath = prefix.TrimEnd('/');

                for (var i = 0; i < segments.Length - 1; i++)
                {
                    folderPath += "/" + segments[i];

                    if (!NameMatches(segments[i], query) || !foundFolders.Add(folderPath))
                        continue;

                    if (await GetFolderAsync(folderPath, ct).ConfigureAwait(false) is { } folder)
                        yield return new YandexDiskFolderItem(folder, api);
                }

                if (NameMatches(file.Name, query))
                    yield return new YandexDiskFileItem(file, api);
            }

            offset += files.Count;

            // The server may cap the page below what was asked; it reports the limit it applied.
            if (files.Count < (page.Limit > 0 ? page.Limit : SearchPageSize))
                yield break;
        }
    }

    /// <summary>
    /// Yandex Disk keeps no folder sizes, so this pages through the flat list of every file on the disk
    /// (<c>resources/files</c>) and adds up those below this folder, a snapshot per page — as many requests as the disk
    /// has thousands of files, whichever folder it starts in. The finished count covers every folder of the disk:
    /// for <see cref="FolderSizesCacheDuration"/> any folder of the same <see cref="IDiskApi"/> is answered from it
    /// at once. A folder with no file at any depth below it is not in the list (0 bytes).
    /// </summary>
    public async IAsyncEnumerable<FolderSizes> MeasureFoldersAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var folder = SearchPrefix(remotePath).TrimEnd('/');

        if (KnownSizes.TryGetValue(api, out var known) && DateTimeOffset.UtcNow - known.CountedAt < FolderSizesCacheDuration)
        {
            yield return known.Of(folder);
            yield break;
        }

        var writes = Writes.GetOrCreateValue(api);
        var writesBefore = Volatile.Read(ref writes.Value);
        var tally = new FolderSizeTally();
        var disk = new Dictionary<string, long>(StringComparer.Ordinal);
        var offset = 0;

        while (true)
        {
            var page = await api.MetaInfo.GetFilesInfoAsync(
                new FilesResourceRequest { Limit = SizesPageSize, Offset = offset }, ct).ConfigureAwait(false);

            var files = page.Items;

            foreach (var file in files)
            {
                if (FolderSizeTally.SubfolderOf(folder, file.Path) is { } subfolder)
                    tally.Add(subfolder, file.Size);

                for (var slash = file.Path.LastIndexOf('/'); slash > 0; slash = file.Path.LastIndexOf('/', slash - 1))
                {
                    var ancestor = file.Path[..slash];
                    disk[ancestor] = disk.GetValueOrDefault(ancestor) + file.Size;
                }
            }

            offset += files.Count;

            if (files.Count == 0 || files.Count < (page.Limit > 0 ? page.Limit : SizesPageSize))
                break;

            yield return tally.Snapshot(isComplete: false);
        }

        if (Volatile.Read(ref writes.Value) == writesBefore)
            KnownSizes.AddOrUpdate(api, new DiskSizes(disk, DateTimeOffset.UtcNow));

        yield return tally.Snapshot(isComplete: true);
    }

    /// <summary>A write through this account: its count of the disk is not true any more.</summary>
    internal static void ForgetSizes(IDiskApi api)
    {
        Interlocked.Increment(ref Writes.GetOrCreateValue(api).Value);
        KnownSizes.Remove(api);
    }

    /// <summary>Bytes below every folder of a disk that has files, by its path (<c>disk:/a/b</c>).</summary>
    private sealed class DiskSizes(Dictionary<string, long> bytes, DateTimeOffset countedAt)
    {
        public DateTimeOffset CountedAt => countedAt;

        public FolderSizes Of(string folder)
        {
            var tally = new FolderSizeTally();

            foreach (var (path, size) in bytes)
            {
                if (FolderSizeTally.SubfolderOf(folder, path, isFolder: true) is { } subfolder &&
                    path.Length == folder.Length + 1 + subfolder.Length)
                    tally.Add(subfolder, size);
            }

            return tally.Snapshot(isComplete: true);
        }
    }

    private static bool NameMatches(string name, string query)
        => name.Contains(query, StringComparison.OrdinalIgnoreCase);

    // The flat list names paths "disk:/a/b.txt"; a provider may have been made for "/a" or "disk:/a/".
    private static string SearchPrefix(string path)
        => (path.StartsWith('/') ? "disk:" + path : path).TrimEnd('/') + "/";

    // Null when the folder was deleted between the file listing and this read.
    private async Task<Resource?> GetFolderAsync(string path, CancellationToken ct)
    {
        try
        {
            var resource = await api.MetaInfo.GetInfoAsync(new ResourceRequest { Path = path, Limit = 1 }, ct)
                .ConfigureAwait(false);
            return resource.Type == ResourceType.Dir ? resource : null;
        }
        catch (YandexApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        ForgetSizes(api);

        try
        {
            var resolvedName = await ResolveFolderNameAsync(name, ct).ConfigureAwait(false);
            var path = CombinePath(remotePath, resolvedName);
            await api.Commands.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
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
        ForgetSizes(api);

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
        ForgetSizes(api);

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

    public async Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName, CancellationToken ct = default) =>
        (await RenameItemAsync(item, newName, ct).ConfigureAwait(false)).Map(_ => true);

    /// <summary>A move within the parent folder, then one read of the moved resource.</summary>
    public async Task<FileOperationResult<IFileProviderItem>> RenameItemAsync(IFileProviderItem item, string newName,
        CancellationToken ct = default)
    {
        ForgetSizes(api);

        try
        {
            var newPath = CombinePath(GetParentPath(item.FullName), newName);
            await api.Commands.MoveAndWaitAsync(
                new MoveFileRequest { From = item.FullName, Path = newPath, Overwrite = false }, ct)
                .ConfigureAwait(false);

            return FileOperationResult<IFileProviderItem>.Success(
                await ReadRenamedAsync(newPath, item is IFolderItem, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileProviderItem>.Failure(ex);
        }
    }

    // The move has happened: a failed read-back must not report it as failed, so it falls back to a placeholder.
    private async Task<IFileProviderItem> ReadRenamedAsync(string path, bool isFolder, CancellationToken ct)
    {
        try
        {
            var resource = await api.MetaInfo.GetInfoAsync(new ResourceRequest { Path = path, Limit = 1 }, ct)
                .ConfigureAwait(false);
            return resource.Type == ResourceType.Dir
                ? new YandexDiskFolderItem(resource, api)
                : new YandexDiskFileItem(resource, api);
        }
        catch (Exception) when (!isFolder)
        {
            return new YandexDiskPlaceholderFileItem(path);
        }
        catch (Exception)
        {
            return new YandexDiskPlaceholderFolderItem(path, api);
        }
    }

    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        ForgetSizes(api);

        try
        {
            if (sourceItem is YandexDiskFileItem ydItem && IsSameAccount(ydItem.Api))
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
        ForgetSizes(api);

        try
        {
            if (sourceItem is YandexDiskFileItem ydItem && IsSameAccount(ydItem.Api))
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
        ForgetSizes(api);

        if (sourceItem is not YandexDiskFolderItem ydFolder || !IsSameAccount(ydFolder.Api))
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
        ForgetSizes(api);

        if (sourceItem is not YandexDiskFolderItem ydFolder || !IsSameAccount(ydFolder.Api))
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
        => await GetResourceTypeAsync(fileName, ct).ConfigureAwait(false) == ResourceType.File;

    public async Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default)
        => await GetResourceTypeAsync(folderName, ct).ConfigureAwait(false) == ResourceType.Dir;

    // Only "not found" means the name is free. Any other error must propagate: answering "free" on a
    // network failure makes the caller pick the existing name, and uploads here overwrite.
    private async Task<ResourceType?> GetResourceTypeAsync(string name, CancellationToken ct)
    {
        try
        {
            var path = CombinePath(remotePath, name);
            var resource = await api.MetaInfo.GetInfoAsync(new ResourceRequest { Path = path }, ct).ConfigureAwait(false);
            return resource.Type;
        }
        catch (YandexApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Items keep the IDiskApi of their account; the same type alone could belong to another account.
    private bool IsSameAccount(IDiskApi itemApi) => ReferenceEquals(itemApi, api);

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
