using FileVault.Core;

namespace FileVault.WebDav;

public sealed class WebDavFolderItem : IFolderItem
{
    internal WebDavFolderItem(WebDavConnection connection, string path, DateTimeOffset changedDate)
    {
        Connection = connection;
        ServerPath = path;
        ChangedDate = changedDate;
    }

    public WebDavConnection Connection { get; }

    /// <summary>The decoded path on the server, from the host root.</summary>
    public string ServerPath { get; }

    public string Name => WebDavPath.GetName(ServerPath);
    public string FullName => Connection.Client.FullName(ServerPath);
    public bool IsHidden => Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate { get; }
    public long? Size => null;

    public IFileProvider CreateProvider() => WebDavFileProvider.ForServerPath(Connection, ServerPath);
}
