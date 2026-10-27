using FileVault.Core;

namespace FileVault.WebDav;

public sealed class WebDavFileItem : IFileItem
{
    internal WebDavFileItem(WebDavConnection connection, string path, long length, DateTimeOffset changedDate)
    {
        Connection = connection;
        ServerPath = path;
        Size = length;
        ChangedDate = changedDate;
    }

    public WebDavConnection Connection { get; }

    /// <summary>The decoded path on the server, from the host root.</summary>
    public string ServerPath { get; }

    public string Name => WebDavPath.GetName(ServerPath);
    public string FullName => Connection.Client.FullName(ServerPath);
    public bool IsHidden => Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate { get; }
    public long Size { get; }
    long? IFileProviderItem.Size => Size;
    public string Extension => Path.GetExtension(Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(Name);

    // Streams straight from the server: the consumer's ReadAsync token stops the download mid-file.
    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var (stream, length) = await Connection.Client.GetAsync(ServerPath, ct).ConfigureAwait(false);
        return (stream, length ?? Size);
    }

    public async Task DeleteAsync(CancellationToken ct = default) =>
        await Connection.Client.DeleteAsync(ServerPath, collection: false, ct).ConfigureAwait(false);
}
