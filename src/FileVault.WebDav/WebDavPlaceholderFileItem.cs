using FileVault.Core;

namespace FileVault.WebDav;

internal sealed class WebDavPlaceholderFileItem(WebDavConnection connection, string path) : IFileItem
{
    public WebDavConnection Connection => connection;
    public string ServerPath => path;

    public string Name => WebDavPath.GetName(path);
    public string FullName => connection.Client.FullName(path);
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long Size => 0;
    long? IFileProviderItem.Size => 0;
    public string Extension => Path.GetExtension(Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(Name);

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
        => Task.FromResult<(Stream, long)>((new MemoryStream(), 0));

    public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
}
