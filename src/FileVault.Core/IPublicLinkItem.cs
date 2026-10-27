namespace FileVault.Core;

/// <summary>
/// Optional: a file or folder its provider can share by a link anyone can open without signing in (Yandex Disk
/// publishing, Google Drive "anyone with the link", OneDrive and Dropbox shared links). Check with
/// <c>item is IPublicLinkItem shareable</c>.
/// </summary>
public interface IPublicLinkItem : IFileProviderItem
{
    /// <summary>
    /// The item's public link, made view-only when it has none yet; an existing link is returned as it is. Sharing
    /// changes the item on the backend, so a client asks or tells the user before calling this.
    /// </summary>
    Task<FileOperationResult<Uri>> GetPublicLinkAsync(CancellationToken ct = default);

    /// <summary>Stops sharing the item: its public link no longer opens. No link → success.</summary>
    Task<FileOperationResult<bool>> RemovePublicLinkAsync(CancellationToken ct = default);
}
