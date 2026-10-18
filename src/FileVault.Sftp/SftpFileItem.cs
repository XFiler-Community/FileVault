using FileVault.Core;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FileVault.Sftp;

public sealed class SftpFileItem(ISftpFile file, SftpClient client) : IFileItem
{
    internal SftpClient Client => client;

    public string Name => file.Name;
    public string FullName => file.FullName;
    public bool IsHidden => file.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => file.LastWriteTime;
    public long Size => file.Length;
    long? IFileProviderItem.Size => file.Length;
    public string Extension => Path.GetExtension(file.Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(file.Name);

    // Streams straight from the server: the consumer's ReadAsync token stops the download mid-file.
    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var stream = await client.OpenAsync(file.FullName, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
        return (stream, file.Length);
    }

    public async Task DeleteAsync(CancellationToken ct = default)
        => await client.DeleteFileAsync(file.FullName, ct).ConfigureAwait(false);
}
