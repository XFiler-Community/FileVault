using FileVault.Core;
using FluentFTP;

namespace FileVault.Ftp;

public sealed class FtpFileItem(FtpListItem item, AsyncFtpClient client) : IFileItem
{
    public string Name => item.Name;
    public string FullName => item.FullName;
    public bool IsHidden => item.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => item.Modified == DateTime.MinValue
        ? DateTimeOffset.UnixEpoch
        : new DateTimeOffset(item.Modified);
    public long Size => item.Size;
    long? IFileProviderItem.Size => item.Size;
    public string Extension => Path.GetExtension(item.Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(item.Name);

    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var stream = new MemoryStream();
        await client.DownloadStream(stream, item.FullName, token: ct).ConfigureAwait(false);
        stream.Position = 0;
        return (stream, stream.Length);
    }

    public async Task DeleteAsync(CancellationToken ct = default)
        => await client.DeleteFile(item.FullName, ct).ConfigureAwait(false);
}
