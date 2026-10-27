using FileVault.Core;

namespace FileVault.OneDrive;

// FullName is "onedrive:<parentId>/<name>". CreateProvider() falls back to the parent folder; the orchestrator navigates
// with the items CreateFolderAsync returns, which carry real ids.
internal sealed class OneDrivePlaceholderFolderItem(OneDriveClient client, string parentId, string name) : IFolderItem
{
    public string Name => name;
    public string FullName => $"{OneDriveClient.Scheme}{parentId}/{name}";
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new OneDriveFileProvider(client, parentId);
}
