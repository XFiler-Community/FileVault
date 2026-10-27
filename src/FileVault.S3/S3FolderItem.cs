using FileVault.Core;

namespace FileVault.S3;

/// <summary>
/// A common prefix of the bucket. S3 keeps no dates for prefixes, so <see cref="ChangedDate"/> is the Unix epoch
/// unless the folder was just made here.
/// </summary>
public sealed class S3FolderItem : IFolderItem
{
    internal S3FolderItem(S3Connection connection, string path, DateTimeOffset changedDate)
    {
        Connection = connection;
        Path = path;
        ChangedDate = changedDate;
    }

    public S3Connection Connection { get; }

    /// <summary>The folder's path from the bucket root, without the trailing slash of its prefix.</summary>
    public string Path { get; }

    public string Name => S3Path.GetName(Path);
    public string FullName => Connection.FullName(Path);
    public bool IsHidden => Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate { get; }
    public long? Size => null;

    public IFileProvider CreateProvider() => new S3FileProvider(Connection, Path);
}
