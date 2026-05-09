using FileVault.Core;

namespace FileVault.YandexDisk;

internal sealed class YandexDiskPlaceholderFileItem(string fullPath) : IFileItem
{
    public string Name => Path.GetFileName(fullPath);
    public string FullName => fullPath;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long Size => 0;
    long? IFileProviderItem.Size => 0;
    public string Extension => Path.GetExtension(fullPath);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(fullPath);

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
        => Task.FromResult<(Stream, long)>((new MemoryStream(), 0));

    public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
}
