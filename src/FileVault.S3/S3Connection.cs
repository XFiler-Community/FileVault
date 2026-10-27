namespace FileVault.S3;

/// <summary>
/// One bucket of an S3-compatible storage and the keys to reach it. Items and providers created from connections of
/// the same account (endpoint, region and access key) count as the same provider, so copies and moves between them
/// stay on the server.
/// </summary>
public sealed class S3Connection
{
    /// <summary>S3 refuses parts under 5 MiB, except the last one of an upload.</summary>
    public const long MinPartSize = 5L * 1024 * 1024;

    /// <summary>The largest object a single CopyObject (and a single part) may copy.</summary>
    public const long MaxCopyObjectSize = 5L * 1024 * 1024 * 1024;

    internal const int MaxParts = 10_000;

    private static readonly Lazy<HttpClient> SharedHttpClient =
        new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private S3Client? _client;

    /// <summary>
    /// The service endpoint without the bucket: <c>https://s3.eu-central-1.amazonaws.com</c>,
    /// <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c>, <c>https://storage.yandexcloud.net</c>,
    /// <c>http://localhost:9000</c>.
    /// </summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The signing region: <c>us-east-1</c>, <c>auto</c> for R2, <c>ru-central1</c> for Yandex.</summary>
    public string Region { get; init; } = "us-east-1";

    public required string Bucket { get; init; }
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }

    /// <summary>The session token of temporary credentials, sent as <c>x-amz-security-token</c>.</summary>
    public string? SessionToken { get; init; }

    /// <summary>
    /// True: <c>https://endpoint/bucket/key</c> (MinIO, most self-hosted servers, bucket names with dots).
    /// False: <c>https://bucket.endpoint/key</c> (AWS's default).
    /// </summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>
    /// The client every request goes through. Null — a shared one without a timeout, since a large part or download
    /// can take longer than the default 100 seconds; give one with an infinite (or long) timeout too.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// Uploads of a known length up to this size are one PUT; larger or of unknown length go in parts of
    /// <see cref="PartSize"/>.
    /// </summary>
    public long MultipartThreshold { get; init; } = 64L * 1024 * 1024;

    /// <summary>Bytes of one multipart part, at least <see cref="MinPartSize"/>; grows when 10 000 parts are not enough.</summary>
    public long PartSize { get; init; } = 16L * 1024 * 1024;

    /// <summary>
    /// Server-side copies of objects larger than this go through a multipart copy; at most
    /// <see cref="MaxCopyObjectSize"/>, the CopyObject limit.
    /// </summary>
    public long MultipartCopyThreshold { get; init; } = MaxCopyObjectSize;

    /// <summary><c>s3://&lt;endpoint host[:port]&gt;/&lt;bucket&gt;/</c> — the FullName of the bucket root.</summary>
    public string RootFullName => $"s3://{Endpoint.Authority}/{Bucket}/";

    /// <summary>The FullName of a key (a folder without its trailing slash); empty is the bucket root.</summary>
    public string FullName(string path) => RootFullName + path;

    /// <summary>A provider for a folder of the bucket; <paramref name="path"/> empty is the bucket root.</summary>
    public S3FileProvider CreateProvider(string path = "") => new(this, path);

    /// <summary>The same account: a server-side copy from one to the other can be signed by either.</summary>
    public bool IsSameAccount(S3Connection other) =>
        ReferenceEquals(this, other)
        || Uri.Compare(Endpoint, other.Endpoint, UriComponents.SchemeAndServer, UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0
        && Region == other.Region
        && AccessKeyId == other.AccessKeyId;

    internal bool IsSameBucket(S3Connection other) => IsSameAccount(other) && Bucket == other.Bucket;

    internal S3Client Client => LazyInitializer.EnsureInitialized(ref _client, () => new S3Client(this,
        HttpClient ?? SharedHttpClient.Value));
}
