using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FileVault.Core;

namespace FileVault.WebDav;

/// <summary>The WebDAV methods over one HttpClient; paths are server paths (<see cref="WebDavPath"/>).</summary>
internal sealed class WebDavClient : IDisposable
{
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod MkCol = new("MKCOL");
    private static readonly HttpMethod Copy = new("COPY");
    private static readonly HttpMethod Move = new("MOVE");

    private const string ListBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:creationdate/></d:prop></d:propfind>""";

    private const string QuotaBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:creationdate/><d:quota-used-bytes/><d:quota-available-bytes/></d:prop></d:propfind>""";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly string _origin;
    private readonly string _fullNamePrefix;

    public WebDavClient(WebDavConnection connection)
    {
        var baseUri = connection.BaseUri;

        _origin = baseUri.GetLeftPart(UriPartial.Authority);
        _fullNamePrefix = (baseUri.Scheme == Uri.UriSchemeHttps ? "webdavs://" : "webdav://") + baseUri.Authority;
        RootPath = WebDavPath.DecodeHref(baseUri.AbsolutePath, baseUri);

        if (connection.UserName is { } user)
            _authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{connection.Password}")));

        if (connection.HttpClient is { } http)
        {
            _http = http;
        }
        else
        {
            _http = connection.Handler is { } handler
                ? new HttpClient(handler, disposeHandler: false)
                : new HttpClient();
            _http.Timeout = Timeout.InfiniteTimeSpan;
            _ownsHttp = true;
        }
    }

    /// <summary>The server path of the connection's base URI.</summary>
    public string RootPath { get; }

    public string FullName(string path) => _fullNamePrefix + (path.Length == 0 ? "/" : path);

    /// <summary>A <c>FullName</c> of this server, or an http(s) URL with its origin.</summary>
    public bool TryParseFullName(string value, out string path)
    {
        if (StartsWithOrigin(value, _fullNamePrefix))
        {
            path = WebDavPath.Normalize(value[_fullNamePrefix.Length..]);
            return true;
        }

        if (StartsWithOrigin(value, _origin))
        {
            path = WebDavPath.Normalize(WebDavPath.DecodeHref(value, new Uri(_origin)));
            return true;
        }

        path = "";
        return false;
    }

    private static bool StartsWithOrigin(string value, string origin) =>
        value.StartsWith(origin, StringComparison.OrdinalIgnoreCase)
        && (value.Length == origin.Length || value[origin.Length] == '/');

    public Uri GetUri(string path, bool collection) =>
        new(_origin + WebDavPath.Encode(path) + (collection || path.Length == 0 ? "/" : ""));

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>The item at a path (Depth 0); null when there is none.</summary>
    public async Task<WebDavResource?> StatAsync(string path, bool collection, bool quota, CancellationToken ct)
    {
        var uri = GetUri(path, collection);
        var resources = await PropFindAsync(uri, path, depth: 0, quota ? QuotaBody : ListBody, notFoundIsNull: true, ct)
            .ConfigureAwait(false);

        if (resources is null)
            return null;

        return resources.Count > 0
            ? resources[0] with { Path = path }
            : throw new WebDavException(HttpStatusCode.MultiStatus, $"The server said nothing about '{path}'.");
    }

    /// <summary>
    /// The items of a folder (Depth 1), with paths built from the folder's own path and each href's last segment:
    /// a reverse proxy that rewrites the prefix of hrefs does not move the items elsewhere.
    /// </summary>
    public async Task<IReadOnlyList<WebDavResource>> ListAsync(string path, CancellationToken ct)
    {
        var uri = GetUri(path, collection: true);
        var resources = await PropFindAsync(uri, path, depth: 1, ListBody, notFoundIsNull: true, ct)
                            .ConfigureAwait(false)
                        ?? throw new DirectoryNotFoundException($"There is no folder '{FullName(path)}'.");

        var self = FindSelf(resources, path);
        var items = new List<WebDavResource>(resources.Count);

        foreach (var resource in resources)
        {
            if (ReferenceEquals(resource, self))
                continue;

            var name = WebDavPath.GetName(resource.Path);

            if (name.Length > 0)
                items.Add(resource with { Path = WebDavPath.Combine(path, name) });
        }

        return items;
    }

    /// <summary>The folder's own entry: the one with its path, else the shortest (servers behind proxies).</summary>
    private static WebDavResource? FindSelf(IReadOnlyList<WebDavResource> resources, string path)
    {
        var normalized = path.Normalize(NormalizationForm.FormC);

        return resources.FirstOrDefault(r => r.Path.Normalize(NormalizationForm.FormC) == normalized)
               ?? resources.Where(r => r.IsCollection).MinBy(r => r.Path.Length);
    }

    public async Task<(Stream Stream, long? Length)> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GetUri(path, collection: false));
        var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        try
        {
            await EnsureSuccessAsync(response, request, path).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return (stream, response.Content.Headers.ContentLength);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    public async Task CreateFolderAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(MkCol, GetUri(path, collection: true));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            throw new WebDavException(response.StatusCode, $"'{FullName(path)}' already exists.");

        await EnsureSuccessAsync(response, request, path).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads with a known length, so servers that refuse chunked PUT (nginx, many NAS) take it; a source of unknown
    /// length is buffered in a temp file first. <paramref name="mustBeNew"/> sends <c>If-None-Match: *</c>.
    /// </summary>
    public async Task PutAsync(string path, Stream source, long totalBytes, IProgress<double>? progress, bool mustBeNew,
        CancellationToken ct)
    {
        long length;

        if (source.CanSeek)
            length = source.Length - source.Position;
        else if (totalBytes > 0)
            length = totalBytes;
        else
        {
            await using var buffer = await TemporaryFileBuffer
                .CreateAsync((target, token) => source.CopyToAsync(target, token), ct).ConfigureAwait(false);
            await PutAsync(path, buffer, buffer.Length, progress, mustBeNew, ct).ConfigureAwait(false);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, GetUri(path, collection: false));
        request.Content = new UploadContent(source, length, progress);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        if (mustBeNew)
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);

        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new WebDavException(response.StatusCode, $"'{FullName(path)}' already exists.");

        await EnsureSuccessAsync(response, request, path).ConfigureAwait(false);
    }

    /// <summary>A missing item counts as deleted.</summary>
    public async Task DeleteAsync(string path, bool collection, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, GetUri(path, collection));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        await EnsureSuccessAsync(response, request, path).ConfigureAwait(false);
    }

    /// <summary>
    /// Server-side COPY or MOVE (a folder with everything in it). False — <paramref name="overwrite"/> is false and
    /// the destination exists (412); nothing was changed.
    /// </summary>
    public async Task<bool> TransferAsync(bool move, string from, string to, bool collection, bool overwrite,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(move ? Move : Copy, GetUri(from, collection));
        request.Headers.TryAddWithoutValidation("Destination", GetUri(to, collection).AbsoluteUri);
        request.Headers.TryAddWithoutValidation("Overwrite", overwrite ? "T" : "F");

        if (collection)
            request.Headers.TryAddWithoutValidation("Depth", "infinity");

        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        if (!overwrite && response.StatusCode == HttpStatusCode.PreconditionFailed)
            return false;

        await EnsureSuccessAsync(response, request, from).ConfigureAwait(false);
        return true;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<WebDavResource>?> PropFindAsync(Uri uri, string path, int depth, string body,
        bool notFoundIsNull, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(PropFind, uri);
        request.Headers.TryAddWithoutValidation("Depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new StringContent(body, Encoding.UTF8, "application/xml");

        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, request, path).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await WebDavMultiStatus.ParseAsync(stream, uri, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion,
        CancellationToken ct)
    {
        if (_authorization is not null)
            request.Headers.Authorization = _authorization;

        return await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
    }

    /// <remarks>207 is an error everywhere but PROPFIND: DELETE / COPY / MOVE of a folder answer it when some items failed.</remarks>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, HttpRequestMessage request, string path)
    {
        var status = response.StatusCode;
        var isPropFind = request.Method == PropFind;

        if ((int)status is >= 200 and < 300 && (status != HttpStatusCode.MultiStatus || isPropFind))
            return;

        var target = FullName(path);
        var detail = $"{request.Method} '{target}' failed: {(int)status} {response.ReasonPhrase}".TrimEnd();

        if (status == HttpStatusCode.Unauthorized)
        {
            var schemes = response.Headers.WwwAuthenticate.Select(h => h.Scheme).ToList();

            if (schemes.Count > 0 && !schemes.Contains("Basic", StringComparer.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"{request.RequestUri?.Host} asks for {string.Join(" / ", schemes)} authentication; FileVault.WebDav supports Basic only.");

            throw new WebDavException(status, _authorization is null
                ? $"{request.RequestUri?.Host} needs a user name and password."
                : $"{request.RequestUri?.Host} refused the user name or password.");
        }

        string message = status switch
        {
            HttpStatusCode.MultiStatus => $"{request.Method} '{target}' failed for some of the items in it.",
            HttpStatusCode.Forbidden => $"Access to '{target}' is denied ({request.Method}).",
            HttpStatusCode.NotFound => $"'{target}' does not exist.",
            HttpStatusCode.Conflict => $"The parent folder of '{target}' or of the destination does not exist.",
            HttpStatusCode.PreconditionFailed => $"The destination of '{target}' already exists.",
            HttpStatusCode.Locked => $"'{target}' is locked.",
            HttpStatusCode.InsufficientStorage => "There is not enough space on the server.",
            HttpStatusCode.RequestEntityTooLarge => $"The server refused '{target}': the file is too large.",
            _ => detail
        };

        // Drain a short error body so the connection can be reused; its text is not shown.
        try
        {
            await response.Content.LoadIntoBufferAsync(64 * 1024).ConfigureAwait(false);
        }
        catch
        {
            // A broken error body changes nothing about the error.
        }

        throw new WebDavException(status, message);
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private sealed class UploadContent(Stream source, long length, IProgress<double>? progress) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
            FileOperationsHelper.CopyAsync(source, length, stream, progress, ct);

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }
}
