using FileVault.Core;

namespace FileVault.Local;

public sealed class LocalDriveItem : IDriveItem
{
    private readonly DriveInfo _drive;
    private readonly DirectoryInfo _dirInfo;

    public LocalDriveItem(DriveInfo drive)
    {
        _drive = drive;
        _dirInfo = drive.RootDirectory;
        TotalSize = drive.IsReady ? drive.TotalSize : 0;
        TotalFreeSpace = drive.IsReady ? drive.TotalFreeSpace : 0;
        IsUserVisible = ComputeIsUserVisible(drive.Name);
    }

    public string Name => _drive.Name;
    public string FullName => _dirInfo.FullName;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => _dirInfo.LastWriteTime;
    public long? Size => null;
    public long TotalSize { get; }
    public long TotalFreeSpace { get; }
    public bool IsUserVisible { get; }

    public IFileProvider CreateProvider() => new LocalFileProvider(_dirInfo.FullName);

    private static bool ComputeIsUserVisible(string name)
    {
        if (OperatingSystem.IsMacOS())
            return name == "/" || name.StartsWith("/Volumes/", StringComparison.Ordinal);
        return true;
    }
}
