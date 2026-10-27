using FileVault.Core;

namespace FileVault.WebDav;

/// <summary>
/// The root folder of a <see cref="WebDavConnection"/> with the account's quota (RFC 4331). Servers that do not
/// report it, or report an unlimited / unknown quota as a negative number, give 0 sizes. Made by
/// <see cref="WebDavConnection.GetDriveAsync"/>.
/// </summary>
public sealed class WebDavDriveItem : IDriveItem
{
    internal WebDavDriveItem(WebDavConnection connection, long? usedBytes, long? availableBytes,
        DateTimeOffset changedDate)
    {
        Connection = connection;
        ChangedDate = changedDate;
        UsedBytes = usedBytes is >= 0 ? usedBytes.Value : 0;
        TotalFreeSpace = availableBytes is >= 0 ? availableBytes.Value : 0;
        TotalSize = availableBytes is >= 0 ? UsedBytes + TotalFreeSpace : 0;
    }

    public WebDavConnection Connection { get; }

    /// <summary><c>quota-used-bytes</c>; 0 when not reported.</summary>
    public long UsedBytes { get; }

    public string Name => Connection.Name;
    public string FullName => Connection.Client.FullName(Connection.RootPath);
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate { get; }
    public long? Size => null;
    public long TotalSize { get; }
    public long TotalFreeSpace { get; }
    public bool IsUserVisible => true;

    public IFileProvider CreateProvider() => WebDavFileProvider.ForServerPath(Connection, Connection.RootPath);
}
