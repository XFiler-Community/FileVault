using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FileVault.Core;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace FileVault.GoogleDrive;

public sealed partial class GoogleDriveFileItem(DriveFile file, DriveService service)
    : IFileRevisionsItem, IThumbnailItem, IPublicLinkItem
{
    private const string RevisionFields =
        "nextPageToken, revisions(id, modifiedTime, size, md5Checksum, lastModifyingUser(displayName))";

    internal DriveFile File => file;
    internal DriveService Service => service;

    public string Name => file.Name;
    public string FullName => $"gdrive:{file.Id}";
    public bool IsHidden => file.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => file.ModifiedTimeDateTimeOffset ?? DateTimeOffset.Now;
    public long Size => file.Size ?? 0;
    long? IFileProviderItem.Size => file.Size;
    public string Extension => Path.GetExtension(file.Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(file.Name);

    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        // A Google Doc is exported, and the export reports a failure instead of throwing: it is downloaded
        // completely before it is handed out, so a failed transfer never looks like a short file to the caller
        // (which may delete the source after copying it).
        if (GoogleDriveMimeTypes.IsWorkspaceType(file.MimeType))
        {
            var export = await TemporaryFileBuffer.CreateAsync(async (destination, token) =>
                GoogleDriveTransfer.EnsureCompleted(await ExportAsync(destination, token).ConfigureAwait(false)),
                ct).ConfigureAwait(false);
            return (export, export.Length);
        }

        // A large file is read as several ranges at once and handed out as they arrive, so a copy uploads while it
        // downloads and its progress moves from the start. Every range is checked for its length and a broken one
        // throws, so this stream never ends early either. A small file, or a server that ignores ranges, is
        // downloaded whole first (the plain download, too, reports a failure instead of throwing).
        var stream = await ParallelDownload.OpenAsync(Size, DownloadRangeAsync,
            async t => await TemporaryFileBuffer.CreateAsync(async (whole, t2) =>
                GoogleDriveTransfer.EnsureCompleted(
                    await service.Files.Get(file.Id).DownloadAsync(whole, t2).ConfigureAwait(false)), t)
                .ConfigureAwait(false),
            ct: ct).ConfigureAwait(false);

        return (stream, stream.CanSeek ? stream.Length : Size);
    }

    private async Task<Stream> DownloadRangeAsync(long offset, int count, CancellationToken ct)
    {
        var range = new MemoryStream(count);
        GoogleDriveTransfer.EnsureCompleted(await service.Files.Get(file.Id)
            .DownloadRangeAsync(range, new RangeHeaderValue(offset, offset + count - 1), ct).ConfigureAwait(false));

        if (range.Length != count)
        {
            throw range.Length == Size
                ? new RangeNotSupportedException("Google Drive sent the whole file instead of a range.")
                : new IOException($"Asked Google Drive for {count} bytes from {offset}, got {range.Length}.");
        }

        range.Position = 0;
        return range;
    }

    private Task<IDownloadProgress> ExportAsync(Stream destination, CancellationToken ct)
    {
        var exportMimeType = GoogleDriveMimeTypes.GetExportMimeType(file.MimeType)
            ?? throw new NotSupportedException(
                $"Google Drive item '{file.Name}' ({file.MimeType}) has no downloadable content.");
        return service.Files.Export(file.Id, exportMimeType).DownloadAsync(destination, ct);
    }

    /// <summary>
    /// Drive keeps the revisions of a binary file for 30 days or 100 revisions unless one is kept forever. A Google
    /// Docs, Sheets or Slides file has revisions, but their content cannot be downloaded: it has none here.
    /// </summary>
    public async Task<IReadOnlyList<FileRevision>> GetRevisionsAsync(CancellationToken ct = default)
    {
        if (GoogleDriveMimeTypes.IsWorkspaceType(file.MimeType))
            return [];

        var revisions = new List<FileRevision>();
        string? pageToken = null;

        do
        {
            var request = service.Revisions.List(file.Id);
            request.Fields = RevisionFields;
            request.PageSize = 1000;
            request.PageToken = pageToken;

            var page = await request.ExecuteAsync(ct).ConfigureAwait(false);

            revisions.AddRange((page.Revisions ?? []).Select(r => new FileRevision(
                r.Id,
                r.ModifiedTimeDateTimeOffset ?? DateTimeOffset.MinValue,
                r.Size,
                r.Md5Checksum,
                r.LastModifyingUser?.DisplayName)));

            pageToken = page.NextPageToken;
        }
        while (pageToken != null);

        return revisions.OrderBy(r => r.ModifiedDate).ToList();
    }

    public async Task<(Stream stream, long totalBytes)> OpenRevisionAsync(FileRevision revision,
        CancellationToken ct = default)
    {
        var stream = await TemporaryFileBuffer.CreateAsync(
            async (destination, token) =>
            {
                var download = await service.Revisions.Get(file.Id, revision.Id).DownloadAsync(destination, token)
                    .ConfigureAwait(false);
                GoogleDriveTransfer.EnsureCompleted(download);
            }, ct).ConfigureAwait(false);
        return (stream, stream.Length);
    }

    /// <summary>
    /// To the Drive trash (<c>trashed: true</c>), not <c>files.delete</c>, which skips it: this is how a move to
    /// another provider removes its source, and a move should leave the file restorable.
    /// </summary>
    public async Task DeleteAsync(CancellationToken ct = default)
        => await service.Files.Update(new DriveFile { Trashed = true }, file.Id).ExecuteAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Shares the file as "anyone with the link" (viewer) and returns its <c>webViewLink</c>; when anyone can open
    /// it already, its link is returned and the sharing is left as it is.
    /// </summary>
    public Task<FileOperationResult<Uri>> GetPublicLinkAsync(CancellationToken ct = default)
        => GoogleDrivePublicLink.GetAsync(service, file.Id, ct);

    /// <summary>
    /// Removes every "anyone" permission of the file. The result is true when a link was removed, false when it had none.
    /// </summary>
    public Task<FileOperationResult<bool>> RemovePublicLinkAsync(CancellationToken ct = default)
        => GoogleDrivePublicLink.RemoveAsync(service, file.Id, ct);

    public bool HasThumbnail => file.ThumbnailLink is not null;

    /// <summary>
    /// The thumbnail link of a listing lives for hours: once it is refused, a fresh one is asked for and tried once.
    /// The link takes the size as its <c>=s&lt;px&gt;</c> suffix.
    /// </summary>
    public async Task<Stream?> OpenThumbnailAsync(int size, CancellationToken ct = default)
    {
        if (file.ThumbnailLink is not { } link)
            return null;

        if (await DownloadThumbnailAsync(link, size, ct).ConfigureAwait(false) is { } thumbnail)
            return thumbnail;

        var request = service.Files.Get(file.Id);
        request.Fields = "thumbnailLink";
        var fresh = await request.ExecuteAsync(ct).ConfigureAwait(false);

        file.ThumbnailLink = fresh.ThumbnailLink;

        if (fresh.ThumbnailLink is null)
            return null;

        return await DownloadThumbnailAsync(fresh.ThumbnailLink, size, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException($"Google Drive refused the thumbnail of '{file.Name}'.");
    }

    /// <summary>Null when the link is refused (expired); other failures throw.</summary>
    private async Task<Stream?> DownloadThumbnailAsync(string link, int size, CancellationToken ct)
    {
        using var response = await service.HttpClient
            .GetAsync(ThumbnailSize().Replace(link, $"=s{size}"), HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return null;

        response.EnsureSuccessStatusCode();

        return new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
    }

    [GeneratedRegex(@"=s\d+$")]
    private static partial Regex ThumbnailSize();
}
