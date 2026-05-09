using Egorozh.YandexDisk.Client;
using FileVault.Core;

namespace FileVault.YandexDisk;

internal sealed class YandexDiskPlaceholderFolderItem(string fullPath, IDiskApi api) : IFolderItem
{
    public string Name => fullPath.TrimEnd('/').Split('/').Last();
    public string FullName => fullPath;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new YandexDiskFileProvider(api, fullPath);
}
