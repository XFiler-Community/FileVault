using Egorozh.YandexDisk.Client;
using Egorozh.YandexDisk.Client.Protocol;
using FileVault.Core;

namespace FileVault.YandexDisk;

public sealed class YandexDiskFolderItem(Resource resource, IDiskApi api) : IFolderItem
{
    public string Name => resource.Name;
    public string FullName => resource.Path;
    public bool IsHidden => resource.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => resource.Modified;
    public long? Size => null;

    public IFileProvider CreateProvider() => new YandexDiskFileProvider(api, resource.Path);
}
