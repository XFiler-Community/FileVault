using FileVault.Core;

namespace FileVault.OneDrive;

// FullName is "onedrive:<parentId>/<name>": a destination that is not (yet) a OneDrive item with an id.
internal sealed class OneDrivePlaceholderFileItem(string parentId, string name) : IFileItem
{
    public string Name => name;
    public string FullName => $"{OneDriveClient.Scheme}{parentId}/{name}";
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
