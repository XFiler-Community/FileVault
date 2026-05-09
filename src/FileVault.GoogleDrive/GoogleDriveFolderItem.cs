using FileVault.Core;
using Google.Apis.Drive.v3;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace FileVault.GoogleDrive;

public sealed class GoogleDriveFolderItem(DriveFile file, DriveService service) : IFolderItem
{
    internal DriveFile File => file;

    public string Name => file.Name;
    public string FullName => $"gdrive:{file.Id}";
    public bool IsHidden => file.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => file.ModifiedTimeDateTimeOffset ?? DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new GoogleDriveFileProvider(service, file.Id);
}
