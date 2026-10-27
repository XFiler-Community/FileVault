namespace FileVault.WebDav;

/// <summary>
/// One account on a WebDAV server: the address of its root folder and the credentials, sent with Basic
/// authentication. Items and providers created from one instance count as the same provider, so copies and moves
/// between them stay on the server (WebDAV COPY / MOVE).
/// <para>
/// Every item's <c>FullName</c> is <c>webdavs://host[:port]/path</c> for an https server and
/// <c>webdav://host[:port]/path</c> for http, where <c>path</c> is the server path, decoded and starting at the
/// host root (it includes the path of <see cref="BaseUri"/>). It is an identity, not a URL: a name with a space,
/// <c>#</c> or <c>%</c> appears as is.
/// </para>
/// </summary>
public sealed class WebDavConnection : IDisposable
{
    private readonly Lazy<WebDavClient> _client;
    private readonly Uri _baseUri = null!;

    public WebDavConnection() => _client = new Lazy<WebDavClient>(() => new WebDavClient(this));

    /// <summary>
    /// The root folder of the account, e.g. <c>https://cloud.example.com/remote.php/dav/files/alice/</c> (Nextcloud),
    /// <c>https://webdav.yandex.ru/</c>, <c>https://nas.local:5006/</c> (Synology). Paths given to this library are
    /// relative to it.
    /// </summary>
    public required Uri BaseUri
    {
        get => _baseUri;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!value.IsAbsoluteUri || (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException($"'{value}' is not an http or https address.", nameof(value));

            _baseUri = value;
        }
    }

    /// <summary>Null — no Authorization header (anonymous server).</summary>
    public string? UserName { get; init; }

    public string? Password { get; init; }

    /// <summary>
    /// A client to send the requests with; the caller owns it. Its <c>Timeout</c> applies to whole transfers, so
    /// for large files set it to <see cref="Timeout.InfiniteTimeSpan"/> and cancel through the token instead.
    /// Null — the connection makes its own client (without a timeout) and disposes it with itself.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// The handler for the connection's own client when <see cref="HttpClient"/> is null (custom certificates, a
    /// proxy); the caller owns it.
    /// </summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>The name of the root folder; null — the host name.</summary>
    public string? DisplayName { get; init; }

    public string Name => DisplayName ?? BaseUri.Host;

    internal WebDavClient Client => _client.Value;

    internal string RootPath => Client.RootPath;

    /// <summary>The <c>FullName</c> of a path relative to <see cref="BaseUri"/>; empty — the root folder.</summary>
    public string GetFullName(string path = "") => Client.FullName(ResolvePath(path));

    /// <summary>A provider for a folder; <paramref name="path"/> as in <see cref="WebDavFileProvider(WebDavConnection, string)"/>.</summary>
    public WebDavFileProvider CreateProvider(string path = "") => new(this, path);

    /// <summary>
    /// The root folder with the account's quota (<c>quota-used-bytes</c> / <c>quota-available-bytes</c>, RFC 4331);
    /// sizes are 0 when the server does not report them. One PROPFIND, so it also checks the address and the
    /// credentials: a failure throws (<see cref="WebDavException"/>, <see cref="NotSupportedException"/> for a server
    /// that takes Digest authentication only, <see cref="HttpRequestException"/> for network errors).
    /// </summary>
    public async Task<WebDavDriveItem> GetDriveAsync(CancellationToken ct = default)
    {
        var root = await Client.StatAsync(RootPath, collection: true, quota: true, ct).ConfigureAwait(false)
                   ?? throw new WebDavException(System.Net.HttpStatusCode.NotFound,
                       $"There is no folder at {BaseUri}.");

        if (!root.IsCollection)
            throw new IOException($"{BaseUri} is a file, not a folder.");

        return new WebDavDriveItem(this, root.QuotaUsedBytes, root.QuotaAvailableBytes,
            root.LastModified ?? DateTimeOffset.MinValue);
    }

    /// <summary>
    /// The server path of a path relative to <see cref="BaseUri"/> (a leading slash changes nothing) or of a
    /// <c>FullName</c> / http(s) URL of this server below <see cref="BaseUri"/>.
    /// </summary>
    internal string ResolvePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var resolved = Client.TryParseFullName(path, out var serverPath)
            ? serverPath
            : path.Contains("://", StringComparison.Ordinal)
                ? throw new ArgumentException($"'{path}' is not a location on {BaseUri}.", nameof(path))
                : WebDavPath.Normalize(RootPath + "/" + path);

        if (!WebDavPath.IsSameOrInside(resolved, RootPath))
            throw new ArgumentException($"'{path}' is outside {BaseUri}.", nameof(path));

        return resolved;
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
            _client.Value.Dispose();
    }
}
