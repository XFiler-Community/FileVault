using XFiler.YandexDisk.Client;
using XFiler.YandexDisk.Client.Protocol;
using FileVault.Core;

namespace FileVault.YandexDisk;

/// <summary>
/// Yandex Disk publishing of a file or folder: a published resource carries its <c>public_url</c>, which opens
/// without signing in and lets anyone view and download it.
/// </summary>
internal static class YandexDiskPublicLink
{
    public static async Task<FileOperationResult<Uri>> GetAsync(IDiskApi api, string path, CancellationToken ct)
    {
        try
        {
            if (await ReadPublicUrlAsync(api, path, ct).ConfigureAwait(false) is { } existing)
                return FileOperationResult<Uri>.Success(existing);

            await api.MetaInfo.PublishAsync(path, ct).ConfigureAwait(false);

            var published = await ReadPublicUrlAsync(api, path, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Yandex Disk published '{path}' but gave it no public link.");
            return FileOperationResult<Uri>.Success(published);
        }
        catch (Exception ex)
        {
            return FileOperationResult<Uri>.Failure(ex);
        }
    }

    /// <summary>True when a link was removed, false when the resource had none.</summary>
    public static async Task<FileOperationResult<bool>> RemoveAsync(IDiskApi api, string path, CancellationToken ct)
    {
        try
        {
            if (await ReadPublicUrlAsync(api, path, ct).ConfigureAwait(false) is null)
                return FileOperationResult<bool>.Success(false);

            await api.MetaInfo.UnpublishAsync(path, ct).ConfigureAwait(false);
            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }

    // The listing's public_url may be stale (published or unpublished since), so the resource is read again.
    private static async Task<Uri?> ReadPublicUrlAsync(IDiskApi api, string path, CancellationToken ct)
    {
        var resource = await api.MetaInfo.GetInfoAsync(new ResourceRequest { Path = path, Limit = 1 }, ct)
            .ConfigureAwait(false);
        return string.IsNullOrEmpty(resource.PublicUrl) ? null : new Uri(resource.PublicUrl);
    }
}
