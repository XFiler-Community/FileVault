using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FileVault.Core;

namespace FileVault.OneDrive;

public sealed partial class OneDriveClient
{
    private static readonly TimeSpan MaxCopyPollInterval = TimeSpan.FromSeconds(5);

    // ── Download ────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole content, buffered in a temp file before it is handed out: a transfer that breaks surfaces here and
    /// never looks like a short file to a caller that deletes the source after copying it.
    /// </summary>
    internal async Task<(Stream stream, long totalBytes)> OpenReadAsync(string id, CancellationToken ct)
    {
        var stream = await TemporaryFileBuffer.CreateAsync(async (destination, token) =>
        {
            using var response = await GetFollowingRedirectAsync($"{ItemPath(id)}/content", token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            await response.Content.CopyToAsync(destination, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return (stream, stream.Length);
    }

    // ── Upload ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads <paramref name="length"/> bytes from the current position of a seekable <paramref name="content"/> as
    /// <paramref name="name"/> in a folder. Up to <see cref="OneDriveClientOptions.SimpleUploadLimit"/> — one PUT;
    /// larger — an upload session in fragments of <see cref="OneDriveClientOptions.UploadChunkSize"/>. Either way the
    /// file appears only when all bytes arrived, so a failed or cancelled upload leaves the old file as it was.
    /// </summary>
    internal async Task<GraphDriveItem> UploadAsync(string parentId, string name, Stream content, long length,
        string conflictBehavior, IProgress<double>? progress, CancellationToken ct)
    {
        if (!content.CanSeek)
            throw new ArgumentException("The upload stream must be seekable.", nameof(content));

        if (length <= _options.SimpleUploadLimit)
        {
            var bytes = new byte[length];
            await content.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);

            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put,
                Resolve($"{ChildPath(parentId, name)}:/content?@microsoft.graph.conflictBehavior={conflictBehavior}"))
            {
                Content = OctetStream(bytes, 0, bytes.Length)
            }, true, false, ct).ConfigureAwait(false);

            var item = await ReadJsonAsync(response, OneDriveJsonContext.Default.GraphDriveItem, ct).ConfigureAwait(false);
            progress?.Report(1);
            return item;
        }

        var session = await SendJsonAsync(HttpMethod.Post, $"{ChildPath(parentId, name)}:/createUploadSession",
            new GraphUploadSessionRequest(new GraphUploadableProperties(conflictBehavior, name)),
            OneDriveJsonContext.Default.GraphUploadSessionRequest, OneDriveJsonContext.Default.GraphUploadSession, ct)
            .ConfigureAwait(false);

        var uploadUrl = new Uri(session.UploadUrl
                                ?? throw new OneDriveApiException(HttpStatusCode.OK, null, "Graph gave no upload URL."));

        try
        {
            return await UploadFragmentsAsync(uploadUrl, content, content.Position, length, progress, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            await CancelUploadSessionAsync(uploadUrl).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<GraphDriveItem> UploadFragmentsAsync(Uri uploadUrl, Stream content, long start, long length,
        IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(_options.UploadChunkSize, length)];
        long offset = 0;
        var resyncs = 0;

        while (true)
        {
            var size = (int)Math.Min(buffer.Length, length - offset);
            content.Position = start + offset;
            await content.ReadExactlyAsync(buffer.AsMemory(0, size), ct).ConfigureAwait(false);

            var from = offset;

            // The upload URL is pre-authenticated: an Authorization header there can be refused with 401.
            using var response = await SendAsync(() =>
            {
                var body = OctetStream(buffer, 0, size);
                body.Headers.ContentRange = new ContentRangeHeaderValue(from, from + size - 1, length);
                return new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = body };
            }, false, true, ct).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK or HttpStatusCode.Created:
                    var item = await ReadJsonAsync(response, OneDriveJsonContext.Default.GraphDriveItem, ct)
                        .ConfigureAwait(false);
                    progress?.Report(1);
                    return item;

                case HttpStatusCode.Accepted:
                    var status = await ReadJsonAsync(response, OneDriveJsonContext.Default.GraphUploadSession, ct)
                        .ConfigureAwait(false);
                    offset = NextExpectedOffset(status.NextExpectedRanges) ?? from + size;
                    progress?.Report((double)offset / length);
                    break;

                // The server already has (part of) this range: ask where it wants to continue
                case HttpStatusCode.RequestedRangeNotSatisfiable when resyncs++ < 3:
                    offset = await GetNextExpectedOffsetAsync(uploadUrl, ct).ConfigureAwait(false);
                    break;

                default:
                    await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
                    throw new OneDriveApiException(response.StatusCode, null,
                        $"Unexpected answer {(int)response.StatusCode} to an upload fragment.");
            }

            if (offset < 0 || offset >= length)
                throw new OneDriveApiException(HttpStatusCode.OK, null,
                    $"The upload session expects byte {offset} of a {length}-byte file.");
        }
    }

    private async Task<long> GetNextExpectedOffsetAsync(Uri uploadUrl, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uploadUrl), false, true, ct)
            .ConfigureAwait(false);
        var status = await ReadJsonAsync(response, OneDriveJsonContext.Default.GraphUploadSession, ct).ConfigureAwait(false);

        return NextExpectedOffset(status.NextExpectedRanges)
               ?? throw new OneDriveApiException(response.StatusCode, null, "The upload session expects no more bytes.");
    }

    /// <summary>The start of the first range of <c>nextExpectedRanges</c> (<c>"26-"</c>, <c>"12345-55232"</c>).</summary>
    internal static long? NextExpectedOffset(IReadOnlyList<string>? ranges)
    {
        if (ranges is not { Count: > 0 })
            return null;

        var first = ranges[0];
        var dash = first.IndexOf('-');

        return long.TryParse(dash < 0 ? first : first[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            ? start
            : null;
    }

    /// <summary>Best effort: Graph drops an abandoned session itself once it expires.</summary>
    private async Task CancelUploadSessionAsync(Uri uploadUrl)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Delete, uploadUrl);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static ByteArrayContent OctetStream(byte[] bytes, int offset, int count)
    {
        var content = new ByteArrayContent(bytes, offset, count);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    // ── Server-side copy ────────────────────────────────────────────────────

    /// <summary>
    /// <c>POST …/copy</c> into a folder of this drive under <paramref name="name"/>, then polls the monitor URL until the
    /// copy is done and returns the new item. The copy never overwrites: a taken name ends as
    /// <see cref="OneDriveApiException"/> with <c>nameAlreadyExists</c> (OneDrive personal ignores
    /// <c>conflictBehavior</c> on copy). Cancelling stops the polling, not the copy on the server.
    /// </summary>
    internal async Task<GraphDriveItem> CopyAsync(string sourceId, string parentId, string name,
        IProgress<double>? progress, CancellationToken ct)
    {
        var root = await GetRootReferenceAsync(ct).ConfigureAwait(false);
        var parent = await ResolveIdAsync(parentId, ct).ConfigureAwait(false);

        Uri monitor;

        using (var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Resolve($"{ItemPath(sourceId)}/copy"))
               {
                   Content = JsonContent.Create(new GraphCopyRequest(new GraphParentReference(parent, root.DriveId), name),
                       OneDriveJsonContext.Default.GraphCopyRequest)
               }, true, false, ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            monitor = response.Headers.Location
                      ?? throw new OneDriveApiException(response.StatusCode, null, "Graph accepted the copy without a monitor URL.");
        }

        var resourceId = await WaitForCopyAsync(monitor, progress, ct).ConfigureAwait(false);

        return await GetJsonAsync($"{ItemPath(resourceId)}?$select={ItemSelect}", OneDriveJsonContext.Default.GraphDriveItem,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls a copy monitor (no token: the URL is short-lived and unique to the caller) until <c>completed</c>; returns
    /// the new item's id. A monitor that redirects to the finished item instead of answering is read from where it led.
    /// </summary>
    internal async Task<string> WaitForCopyAsync(Uri monitor, IProgress<double>? progress, CancellationToken ct)
    {
        var interval = _options.CopyPollInterval;

        while (true)
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, monitor), false, true, ct)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location
                && ItemIdFromUrl(location) is { } redirectedId)
                return redirectedId;

            if (response.RequestMessage?.RequestUri is { } landed && landed != monitor
                && ItemIdFromUrl(landed) is { } landedId)
                return landedId;

            var status = await ReadJsonAsync(response, OneDriveJsonContext.Default.GraphAsyncJobStatus, ct)
                .ConfigureAwait(false);

            switch (status.Status)
            {
                case "completed":
                    progress?.Report(1);
                    return status.ResourceId
                           ?? throw new OneDriveApiException(response.StatusCode, null, "The copy completed without an item id.");

                case "failed" or "cancelled" or "deleteFailed":
                    var code = status.Error?.Code;
                    throw new OneDriveApiException(code == "nameAlreadyExists" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest,
                        code, $"The copy {status.Status}{(status.Error?.Message is { } m ? $": {m}" : ".")}");
            }

            if (status.PercentageComplete is { } percent)
                progress?.Report(Math.Clamp(percent / 100, 0, 0.99));

            await Delay(interval, ct).ConfigureAwait(false);
            interval = interval * 2 > MaxCopyPollInterval ? MaxCopyPollInterval : interval * 2;
        }
    }

    /// <summary>The id after <c>/items/</c> in a Graph or SharePoint item URL.</summary>
    internal static string? ItemIdFromUrl(Uri url)
    {
        var path = url.IsAbsoluteUri ? url.AbsolutePath : url.OriginalString;
        var at = path.LastIndexOf("/items/", StringComparison.OrdinalIgnoreCase);

        if (at < 0)
            return null;

        var id = path[(at + "/items/".Length)..];
        var end = id.IndexOfAny(['/', '?', ':']);

        return Uri.UnescapeDataString(end < 0 ? id : id[..end]) is { Length: > 0 } result ? result : null;
    }
}
