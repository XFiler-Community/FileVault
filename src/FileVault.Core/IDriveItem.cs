namespace FileVault.Core;

public interface IDriveItem : IFolderItem
{
    long TotalSize { get; }
    long TotalFreeSpace { get; }
}
