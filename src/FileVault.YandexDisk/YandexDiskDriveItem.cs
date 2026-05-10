using Egorozh.YandexDisk.Client;
using FileVault.Core;

namespace FileVault.YandexDisk;

public sealed class YandexDiskDriveItem(IDiskApi api, long totalSize, long usedSize) : IDriveItem
{
    private const string YandexDiskRoot = "disk:/";

    public string Name => "Яндекс Диск";
    public string FullName => YandexDiskRoot;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;
    public long TotalSize => totalSize;
    public long TotalFreeSpace => totalSize - usedSize;
    public bool IsUserVisible => true;

    public IFileProvider CreateProvider() => new YandexDiskFileProvider(api, YandexDiskRoot);
}
