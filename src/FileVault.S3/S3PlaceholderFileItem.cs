using FileVault.Core;

namespace FileVault.S3;

internal sealed class S3PlaceholderFileItem(string fullName, string name) : IFileItem
{
    public string Name => name;
    public string FullName => fullName;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long Size => 0;
    long? IFileProviderItem.Size => 0;
    public string Extension => Path.GetExtension(name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
        => Task.FromResult<(Stream, long)>((new MemoryStream(), 0));

    public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
}
