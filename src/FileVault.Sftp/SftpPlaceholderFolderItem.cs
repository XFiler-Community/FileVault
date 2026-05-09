using FileVault.Core;
using Renci.SshNet;

namespace FileVault.Sftp;

internal sealed class SftpPlaceholderFolderItem(string fullName, SftpClient client) : IFolderItem
{
    public string Name => Path.GetFileName(fullName.TrimEnd('/'));
    public string FullName => fullName;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new SftpFileProvider(client, fullName);
}
