using FileVault.Core;

namespace FileVault.S3;

/// <summary>An object of a bucket. <see cref="ChangedDate"/> is S3's LastModified: when the object was last written.</summary>
public sealed class S3FileItem : IFileItem
{
    internal S3FileItem(S3Connection connection, string key, long size, DateTimeOffset changedDate, string? eTag)
    {
        Connection = connection;
        Key = key;
        Size = size;
        ChangedDate = changedDate;
        ETag = eTag;
    }

    public S3Connection Connection { get; }

    /// <summary>The object key, from the bucket root.</summary>
    public string Key { get; }

    /// <summary>The ETag as listed: the MD5 of the content for single-PUT objects, opaque for multipart ones.</summary>
    public string? ETag { get; }

    public string Name => S3Path.GetName(Key);
    public string FullName => Connection.FullName(Key);
    public bool IsHidden => Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate { get; }
    public long Size { get; }
    long? IFileProviderItem.Size => Size;
    public string Extension => Path.GetExtension(Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(Name);

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
        Connection.Client.GetAsync(Key, ct);

    public Task DeleteAsync(CancellationToken ct = default) => Connection.Client.DeleteAsync(Key, ct);
}
