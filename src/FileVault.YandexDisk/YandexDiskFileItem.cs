using System.Text.RegularExpressions;
using XFiler.YandexDisk.Client;
using XFiler.YandexDisk.Client.Clients;
using XFiler.YandexDisk.Client.Protocol;
using FileVault.Core;

namespace FileVault.YandexDisk;

public sealed partial class YandexDiskFileItem(Resource resource, IDiskApi api) : IThumbnailItem, IPublicLinkItem
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

    /// <summary>Publishes the file on Yandex Disk; an already published file keeps its link.</summary>
    public Task<FileOperationResult<Uri>> GetPublicLinkAsync(CancellationToken ct = default)
        => YandexDiskPublicLink.GetAsync(api, resource.Path, ct);

    /// <summary>Unpublishes the file. The result is true when a link was removed, false when it had none.</summary>
    public Task<FileOperationResult<bool>> RemovePublicLinkAsync(CancellationToken ct = default)
        => YandexDiskPublicLink.RemoveAsync(api, resource.Path, ct);

    public bool HasThumbnail => resource.Preview is not null;

    /// <summary>
    /// The listing's preview link is drawn at its default size (S, 150 px); its <c>size</c> parameter asks for
    /// another of Yandex's sizes. Should the larger one be refused, the link is used as it came.
    /// </summary>
    public async Task<Stream?> OpenThumbnailAsync(int size, CancellationToken ct = default)
    {
        if (resource.Preview is not { } preview)
            return null;

        var sized = PreviewSize().Replace(preview, $"${{prefix}}{PreviewSizeName(size)}");

        try
        {
            return await DownloadPreviewAsync(sized, ct).ConfigureAwait(false);
        }
        catch (YandexApiException) when (sized != preview)
        {
            return await DownloadPreviewAsync(preview, ct).ConfigureAwait(false);
        }
    }

    private async Task<Stream> DownloadPreviewAsync(string link, CancellationToken ct)
    {
        (var stream, _) = await api.Files.DownloadFastAsync(new Link { Href = link }, ct).ConfigureAwait(false);

        await using (stream)
        {
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            buffer.Position = 0;
            return buffer;
        }
    }

    private static string PreviewSizeName(int size) => size switch
    {
        <= 150 => "S",
        <= 300 => "M",
        <= 500 => "L",
        <= 800 => "XL",
        <= 1024 => "XXL",
        _ => "XXXL"
    };

    [GeneratedRegex(@"(?<prefix>[?&]size=)[^&]*")]
    private static partial Regex PreviewSize();
}
