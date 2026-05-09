using FileVault.Core;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FileVault.Sftp;

public sealed class SftpFolderItem(ISftpFile file, SftpClient client) : IFolderItem
{
    internal SftpClient Client => client;

    public string Name => file.Name;
    public string FullName => file.FullName;
    public bool IsHidden => file.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => file.LastWriteTime;
    public long? Size => null;

    public IFileProvider CreateProvider() => new SftpFileProvider(client, file.FullName);
}
