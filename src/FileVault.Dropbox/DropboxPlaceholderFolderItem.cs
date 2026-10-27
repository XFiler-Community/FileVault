using FileVault.Core;

namespace FileVault.Dropbox;

internal sealed class DropboxPlaceholderFolderItem(DropboxClient client, string path) : IFolderItem
{
    public string Name => DropboxPath.GetName(path);
    public string FullName => DropboxPath.Format(path);
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new DropboxFileProvider(client, DropboxPath.Parent(path));
}
