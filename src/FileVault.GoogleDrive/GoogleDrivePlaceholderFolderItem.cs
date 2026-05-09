using FileVault.Core;
using Google.Apis.Drive.v3;

namespace FileVault.GoogleDrive;

// Returned by ResolveDestinationFolderAsync and TryMoveFolderAsync.
// FullName encodes "gdrive:<parentId>/<folderName>" — a synthetic path, not a Drive file ID.
// CreateProvider() returns a provider for the parent folder as a best-effort fallback;
// in practice the orchestrator uses CreateFolderAsync() results (with real IDs) for navigation.
internal sealed class GoogleDrivePlaceholderFolderItem(string parentId, string folderName, DriveService service) : IFolderItem
{
    public string Name => folderName;
    public string FullName => $"gdrive:{parentId}/{folderName}";
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new GoogleDriveFileProvider(service, parentId);
}
