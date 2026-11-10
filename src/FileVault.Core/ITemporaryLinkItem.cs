namespace FileVault.Core;

/// <summary>
/// Optional: a file its provider can hand out as a short-lived link that downloads it without any credentials (Yandex
/// Disk's download link, Dropbox's temporary link, OneDrive's pre-authenticated download URL, an S3 presigned URL).
/// Another cloud can then fetch the file by itself (<see cref="UrlTransfer"/>), so a copy between two clouds does not
/// pass through this computer. Check with <c>item is ITemporaryLinkItem linked</c>.
/// </summary>
public interface ITemporaryLinkItem : IFileItem
{
    /// <summary>
    /// A link that downloads this file without credentials for a while (minutes to hours, as the backend decides), or
    /// null when this file has none. Unlike <see cref="IPublicLinkItem"/> it changes nothing on the backend, but whoever
    /// holds the link can read the file until it expires: hand it only to the service the file is going to.
    /// </summary>
    Task<Uri?> GetTemporaryLinkAsync(CancellationToken ct = default);
}
