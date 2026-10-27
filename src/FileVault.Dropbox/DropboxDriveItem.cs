using FileVault.Core;

namespace FileVault.Dropbox;

/// <summary>The root of a Dropbox account (<c>dropbox:/</c>) with its quota, as of <see cref="DropboxClient.GetDriveAsync"/>.</summary>
public sealed class DropboxDriveItem(DropboxClient client, DropboxSpaceUsage usage) : IDriveItem
{
    public DropboxClient Client => client;
    public DropboxSpaceUsage Usage => usage;

    public string Name => "Dropbox";
    public string FullName => DropboxPath.Format("");
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => default;
    public long? Size => null;
    public long TotalSize => usage.Allocated;
    public long TotalFreeSpace => usage.Free;
    public bool IsUserVisible => true;

    public IFileProvider CreateProvider() => new DropboxFileProvider(client, "");
}
