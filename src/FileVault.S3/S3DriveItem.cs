using FileVault.Core;

namespace FileVault.S3;

/// <summary>
/// The bucket of a connection as a drive. S3 reports neither a quota nor free space, so both sizes are 0.
/// </summary>
public sealed class S3DriveItem(S3Connection connection) : IDriveItem
{
    public S3Connection Connection => connection;

    public string Name => connection.Bucket;
    public string FullName => connection.RootFullName;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.UnixEpoch;
    public long? Size => null;
    public long TotalSize => 0;
    public long TotalFreeSpace => 0;
    public bool IsUserVisible => true;

    public IFileProvider CreateProvider() => new S3FileProvider(connection);
}
