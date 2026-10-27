using FileVault.Core;

namespace FileVault.Dropbox;

/// <summary>
/// A file in Dropbox. <see cref="ChangedDate"/> is <c>client_modified</c> (the time the uploading app gave, Dropbox's
/// choice for display), else <c>server_modified</c>. <see cref="DeleteAsync"/> uses <c>delete_v2</c>: the file stays
/// restorable for the account's retention period.
/// </summary>
public sealed class DropboxFileItem(DropboxClient client, DropboxEntry entry) : IThumbnailItem, IPublicLinkItem
{
    // get_thumbnail_v2 draws images (jpg, jpeg, png, tiff, tif, gif, webp, ppm, bmp; up to 20 MB) and, in practice,
    // HEIC photos and common videos; a refused one returns null from OpenThumbnailAsync
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".gif", ".webp", ".ppm", ".bmp", ".heic",
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".3gp", ".wmv"
    };

    public DropboxClient Client => client;
    public DropboxEntry Entry => entry;
    public string Path => entry.Path;

    public string Name => entry.Name;
    public string FullName => DropboxPath.Format(entry.Path);
    public bool IsHidden => entry.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => entry.ClientModified ?? entry.ServerModified ?? default;
    public long Size => entry.Size;
    long? IFileProviderItem.Size => entry.Size;
    public string Extension => System.IO.Path.GetExtension(entry.Name);
    public string NameWithoutExtension => System.IO.Path.GetFileNameWithoutExtension(entry.Name);

    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var stream = await client.DownloadAsync(entry.Path, ct).ConfigureAwait(false);
        return (stream, stream.Length);
    }

    public Task DeleteAsync(CancellationToken ct = default) => client.DeleteAsync(entry.Path, ct);

    public bool HasThumbnail => ThumbnailExtensions.Contains(Extension);

    public async Task<Stream?> OpenThumbnailAsync(int size, CancellationToken ct = default) =>
        HasThumbnail ? await client.GetThumbnailAsync(entry.Path, size, ct).ConfigureAwait(false) : null;

    public async Task<FileOperationResult<Uri>> GetPublicLinkAsync(CancellationToken ct = default)
    {
        try
        {
            return FileOperationResult<Uri>.Success(await client.GetPublicLinkAsync(entry.Path, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return FileOperationResult<Uri>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<bool>> RemovePublicLinkAsync(CancellationToken ct = default)
    {
        try
        {
            await client.RemovePublicLinksAsync(entry.Path, ct).ConfigureAwait(false);
            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }
}
