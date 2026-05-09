using FileVault.Core;
using FluentFTP;

namespace FileVault.Ftp;

internal sealed class FtpPlaceholderFolderItem(string fullName, AsyncFtpClient client) : IFolderItem
{
    public string Name => Path.GetFileName(fullName.TrimEnd('/'));
    public string FullName => fullName;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new FtpFileProvider(client, fullName);
}
