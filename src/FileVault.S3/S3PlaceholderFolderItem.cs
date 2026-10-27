using FileVault.Core;

namespace FileVault.S3;

internal sealed class S3PlaceholderFolderItem(S3Connection connection, string path) : IFolderItem
{
    public string Name => S3Path.GetName(path);
    public string FullName => connection.FullName(path);
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new S3FileProvider(connection, S3Path.Parent(path));
}
