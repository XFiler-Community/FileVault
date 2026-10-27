using XFiler.YandexDisk.Client;
using XFiler.YandexDisk.Client.Protocol;
using FileVault.Core;

namespace FileVault.YandexDisk;

public sealed class YandexDiskFolderItem(Resource resource, IDiskApi api) : IFolderItem, IPublicLinkItem
{
    internal IDiskApi Api => api;

    public string Name => resource.Name;
    public string FullName => resource.Path;
    public bool IsHidden => resource.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => resource.Modified;
    public long? Size => null;

    public IFileProvider CreateProvider() => new YandexDiskFileProvider(api, resource.Path);

    /// <summary>Publishes the folder on Yandex Disk; an already published folder keeps its link.</summary>
    public Task<FileOperationResult<Uri>> GetPublicLinkAsync(CancellationToken ct = default)
        => YandexDiskPublicLink.GetAsync(api, resource.Path, ct);

    /// <summary>Unpublishes the folder. The result is true when a link was removed, false when it had none.</summary>
    public Task<FileOperationResult<bool>> RemovePublicLinkAsync(CancellationToken ct = default)
        => YandexDiskPublicLink.RemoveAsync(api, resource.Path, ct);
}
