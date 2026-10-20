using XFiler.YandexDisk.Client;
using XFiler.YandexDisk.Client.Clients;
using XFiler.YandexDisk.Client.Protocol;
using FileVault.Core;

namespace FileVault.YandexDisk;

public sealed class YandexDiskFileItem(Resource resource, IDiskApi api) : IFileItem
{
    internal IDiskApi Api => api;

    public string Name => resource.Name;
    public string FullName => resource.Path;
    public bool IsHidden => resource.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => resource.Modified;
    public long Size => resource.Size;
    long? IFileProviderItem.Size => resource.Size;
    public string Extension => Path.GetExtension(resource.Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(resource.Name);

    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var link = await api.Files.GetDownloadLinkAsync(resource.Path, ct).ConfigureAwait(false);
        (var stream, long totalBytes) = await api.Files.DownloadFastAsync(link, ct).ConfigureAwait(false);
        return (stream, totalBytes);
    }

    public async Task DeleteAsync(CancellationToken ct = default)
        => await api.Commands.DeleteAndWaitAsync(
            new DeleteFileRequest { Path = resource.Path, Permanently = true }, ct).ConfigureAwait(false);
}
