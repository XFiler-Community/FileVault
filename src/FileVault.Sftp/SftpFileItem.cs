using FileVault.Core;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FileVault.Sftp;

public sealed class SftpFileItem(ISftpFile file, SftpClient client) : IFileItem
{
    public string Name => file.Name;
    public string FullName => file.FullName;
    public bool IsHidden => file.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => file.LastWriteTime;
    public long Size => file.Length;
    long? IFileProviderItem.Size => file.Length;
    public string Extension => Path.GetExtension(file.Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(file.Name);

    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var memory = new MemoryStream();
        await Task.Run(() =>
        {
            using var sftp = client.OpenRead(file.FullName);
            sftp.CopyTo(memory);
        }, ct).ConfigureAwait(false);
        memory.Position = 0;
        return (memory, memory.Length);
    }

    public async Task DeleteAsync(CancellationToken ct = default)
        => await Task.Run(() => client.DeleteFile(file.FullName), ct).ConfigureAwait(false);
}
