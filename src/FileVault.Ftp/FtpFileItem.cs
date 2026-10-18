using FileVault.Core;
using FluentFTP;

namespace FileVault.Ftp;

public sealed class FtpFileItem(FtpListItem item, AsyncFtpClient client) : IFileItem
{
    internal AsyncFtpClient Client => client;

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

    // Not FtpClient.OpenRead: a live data stream keeps the control connection busy, and copying within the
    // same server uploads through that connection while the source is still being read.
    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var stream = await TemporaryFileBuffer.CreateAsync(
            async (destination, token) =>
            {
                if (!await client.DownloadStream(destination, item.FullName, token: token).ConfigureAwait(false))
                    throw new IOException($"FTP download of '{item.FullName}' failed.");
            }, ct).ConfigureAwait(false);
        return (stream, stream.Length);
    }

    public async Task DeleteAsync(CancellationToken ct = default)
        => await client.DeleteFile(item.FullName, ct).ConfigureAwait(false);
}
