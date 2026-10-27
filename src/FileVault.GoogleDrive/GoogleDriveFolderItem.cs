using FileVault.Core;
using Google.Apis.Drive.v3;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace FileVault.GoogleDrive;

public sealed class GoogleDriveFolderItem(DriveFile file, DriveService service) : IFolderItem, IPublicLinkItem
{
    internal DriveFile File => file;
    internal DriveService Service => service;

    public string Name => file.Name;
    public string FullName => $"gdrive:{file.Id}";
    public bool IsHidden => file.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => file.ModifiedTimeDateTimeOffset ?? DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new GoogleDriveFileProvider(service, file.Id);

    /// <summary>
    /// Shares the folder as "anyone with the link" (viewer) and returns its <c>webViewLink</c>; when anyone can open
    /// it already, its link is returned and the sharing is left as it is.
    /// </summary>
    public Task<FileOperationResult<Uri>> GetPublicLinkAsync(CancellationToken ct = default)
        => GoogleDrivePublicLink.GetAsync(service, file.Id, ct);

    /// <summary>
    /// Removes every "anyone" permission of the folder. The result is true when a link was removed, false when it had none.
    /// </summary>
    public Task<FileOperationResult<bool>> RemovePublicLinkAsync(CancellationToken ct = default)
        => GoogleDrivePublicLink.RemoveAsync(service, file.Id, ct);
}
