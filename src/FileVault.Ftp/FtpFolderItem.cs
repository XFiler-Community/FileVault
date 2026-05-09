using FileVault.Core;
using FluentFTP;

namespace FileVault.Ftp;

public sealed class FtpFolderItem(FtpListItem item, AsyncFtpClient client) : IFolderItem
{
    internal AsyncFtpClient Client => client;

    public string Name => item.Name;
    public string FullName => item.FullName;
    public bool IsHidden => item.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => item.Modified == DateTime.MinValue
        ? DateTimeOffset.UnixEpoch
        : new DateTimeOffset(item.Modified);
    public long? Size => null;

    public IFileProvider CreateProvider() => new FtpFileProvider(client, item.FullName);
}
