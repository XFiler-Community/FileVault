namespace FileVault.Core;

/// <summary>
/// Optional: a file whose provider keeps a small picture of it (Google Drive thumbnails, Yandex Disk previews), so a
/// client can show one without downloading the file. Check with <c>item is IThumbnailItem { HasThumbnail: true }</c>.
/// </summary>
public interface IThumbnailItem : IFileItem
{
    /// <summary>The provider has a picture of this file. Known from the listing: asking costs no request.</summary>
    bool HasThumbnail { get; }

    /// <summary>
    /// A JPEG or PNG whose longer side is about <paramref name="size"/> px: the provider rounds to the sizes it
    /// draws. Null when the file has no picture. The caller owns the stream.
    /// </summary>
    Task<Stream?> OpenThumbnailAsync(int size, CancellationToken ct = default);
}
