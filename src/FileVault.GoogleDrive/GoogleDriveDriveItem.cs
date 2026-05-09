using FileVault.Core;
using Google.Apis.Drive.v3;

namespace FileVault.GoogleDrive;

public sealed class GoogleDriveDriveItem(DriveService service, long totalSize, long usedSize) : IDriveItem
{
    private const string GoogleDriveRoot = "root";

    public string Name => "Google Drive";
    public string FullName => $"gdrive:{GoogleDriveRoot}";
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;
    public long TotalSize => totalSize;
    public long TotalFreeSpace => totalSize - usedSize;

    public IFileProvider CreateProvider() => new GoogleDriveFileProvider(service, GoogleDriveRoot);
}
