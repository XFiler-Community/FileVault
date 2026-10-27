using FileVault.Core;

namespace FileVault.WebDav;

internal sealed class WebDavPlaceholderFolderItem(WebDavConnection connection, string path) : IFolderItem
{
    public WebDavConnection Connection => connection;
    public string ServerPath => path;

    public string Name => WebDavPath.GetName(path);
    public string FullName => connection.Client.FullName(path);
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;

    // The path is known exactly (the folder was just made or resolved), so it opens itself rather than its parent.
    public IFileProvider CreateProvider() => WebDavFileProvider.ForServerPath(connection, path);
}
