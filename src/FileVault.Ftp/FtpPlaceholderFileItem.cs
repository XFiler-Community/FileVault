using FileVault.Core;

namespace FileVault.Ftp;

internal sealed class FtpPlaceholderFileItem(string fullName) : IFileItem
{
    public string Name => Path.GetFileName(fullName);
    public string FullName => fullName;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long Size => 0;
    long? IFileProviderItem.Size => 0;
    public string Extension => Path.GetExtension(fullName);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(fullName);

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
        => Task.FromResult<(Stream, long)>((new MemoryStream(), 0));

    public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
}
