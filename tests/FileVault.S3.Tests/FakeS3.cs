using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace FileVault.S3.Tests;

/// <summary>
/// An in-memory S3 behind an <see cref="HttpMessageHandler"/>: keys are opaque strings (folder markers included),
/// every request's SigV4 signature is checked against the raw path and query that went on the wire, and listings
/// page by <see cref="PageSize"/>. Path-style and virtual-hosted requests both work.
/// </summary>
internal sealed class FakeS3 : HttpMessageHandler
{
    public const string AccessKey = "AKFAKEEXAMPLE";
    public const string SecretKey = "fake/secret+key";
    public const string EndpointHost = "s3.fake.test";

    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly Dictionary<string, SortedDictionary<string, byte[]>> _buckets = new();
    private readonly Dictionary<string, (string Bucket, string Key, SortedDictionary<int, byte[]> Parts)> _uploads = new();
    private int _nextUpload;

    public int PageSize { get; set; } = 1000;
    public bool SupportsUploadPartCopy { get; set; } = true;

    /// <summary>"METHOD /path?query" of every request, plus " copy" when it carried x-amz-copy-source.</summary>
    public List<string> Requests { get; } = [];

    public IReadOnlyCollection<string> OpenUploads => _uploads.Keys;

    public S3Connection Connect(string bucket, bool pathStyle = true, long? multipartThreshold = null,
        long? partSize = null, long? multipartCopyThreshold = null)
    {
        lock (_buckets)
            _buckets.TryAdd(bucket, new SortedDictionary<string, byte[]>(StringComparer.Ordinal));

        var defaults = new S3Connection
            { Endpoint = new Uri("http://x"), Bucket = "x", AccessKeyId = "x", SecretAccessKey = "x" };

        return new S3Connection
        {
            Endpoint = new Uri($"http://{EndpointHost}"),
            Region = "us-east-1",
            Bucket = bucket,
            AccessKeyId = AccessKey,
            SecretAccessKey = SecretKey,
            ForcePathStyle = pathStyle,
            HttpClient = new HttpClient(this, disposeHandler: false),
            MultipartThreshold = multipartThreshold ?? defaults.MultipartThreshold,
            PartSize = partSize ?? defaults.PartSize,
            MultipartCopyThreshold = multipartCopyThreshold ?? defaults.MultipartCopyThreshold
        };
    }

    public void Put(string bucket, string key, byte[] content)
    {
        lock (_buckets)
            _buckets[bucket][key] = content;
    }

    public byte[]? Get(string bucket, string key)
    {
        lock (_buckets)
            return _buckets[bucket].GetValueOrDefault(key);
    }

    public IReadOnlyList<string> Keys(string bucket)
    {
        lock (_buckets)
            return _buckets[bucket].Keys.ToList();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var uri = request.RequestUri!;
        var rawPath = uri.AbsolutePath;
        var rawQuery = uri.Query.TrimStart('?');
        var host = request.Headers.Host ?? uri.Authority;

        lock (_buckets)
        {
            Requests.Add($"{request.Method} {rawPath}{(rawQuery.Length > 0 ? "?" + rawQuery : "")}" +
                         (request.Headers.Contains("x-amz-copy-source") ? " copy" : ""));

            if (!SignatureMatches(request, host, rawPath, rawQuery, body))
                return Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", "The signature does not match.");

            string bucket, key;

            if (host == EndpointHost)
            {
                var path = rawPath.TrimStart('/');
                var slash = path.IndexOf('/');
                bucket = Uri.UnescapeDataString(slash < 0 ? path : path[..slash]);
                key = slash < 0 ? "" : Uri.UnescapeDataString(path[(slash + 1)..]);
            }
            else
            {
                bucket = host[..host.IndexOf('.')];
                key = Uri.UnescapeDataString(rawPath.TrimStart('/'));
            }

            if (!_buckets.TryGetValue(bucket, out var objects))
                return Error(HttpStatusCode.NotFound, "NoSuchBucket", "The bucket does not exist.");

            var query = ParseQuery(rawQuery);
            var method = request.Method.Method;

            if (key.Length == 0)
            {
                return method switch
                {
                    "GET" when query.GetValueOrDefault("list-type") == "2" => List(bucket, objects, query),
                    "POST" when query.ContainsKey("delete") => DeleteMany(objects, request, body),
                    _ => Error(HttpStatusCode.NotImplemented, "NotImplemented", $"{method} on a bucket.")
                };
            }

            var copySource = request.Headers.TryGetValues("x-amz-copy-source", out var sources)
                ? sources.Single()
                : null;

            switch (method)
            {
                case "HEAD":
                case "GET":
                    if (!objects.TryGetValue(key, out var data))
                        return method == "HEAD"
                            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) }
                            : Error(HttpStatusCode.NotFound, "NoSuchKey", "The specified key does not exist.");

                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(method == "HEAD" ? [] : data)
                    };
                    response.Content.Headers.ContentLength = data.Length;
                    response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                    response.Headers.TryAddWithoutValidation("ETag", ETag(data));
                    return response;

                case "PUT" when query.TryGetValue("uploadId", out var uploadId):
                    if (!_uploads.TryGetValue(uploadId, out var upload))
                        return Error(HttpStatusCode.NotFound, "NoSuchUpload", "No such upload.");

                    var partNumber = int.Parse(query["partNumber"], CultureInfo.InvariantCulture);

                    if (copySource is null)
                    {
                        upload.Parts[partNumber] = body;
                        var partResponse = Ok();
                        partResponse.Headers.TryAddWithoutValidation("ETag", ETag(body));
                        return partResponse;
                    }

                    if (!SupportsUploadPartCopy)
                        return Error(HttpStatusCode.NotImplemented, "NotImplemented", "UploadPartCopy.");

                    var source = ReadCopySource(copySource);

                    if (source is null)
                        return Error(HttpStatusCode.NotFound, "NoSuchKey", "No copy source.");

                    var range = request.Headers.GetValues("x-amz-copy-source-range").Single()["bytes=".Length..]
                        .Split('-').Select(long.Parse).ToArray();
                    var slice = source[(int)range[0]..(int)(range[1] + 1)];
                    upload.Parts[partNumber] = slice;

                    return Xml(new XElement(Ns + "CopyPartResult", new XElement(Ns + "ETag", ETag(slice))));

                case "PUT" when copySource is not null:
                    var copied = ReadCopySource(copySource);

                    if (copied is null)
                        return Error(HttpStatusCode.NotFound, "NoSuchKey", "No copy source.");

                    objects[key] = copied;
                    return Xml(new XElement(Ns + "CopyObjectResult", new XElement(Ns + "ETag", ETag(copied))));

                case "PUT":
                    objects[key] = body;
                    return Ok();

                case "POST" when query.ContainsKey("uploads"):
                    var id = $"upload-{++_nextUpload}";
                    _uploads[id] = (bucket, key, new SortedDictionary<int, byte[]>());

                    return Xml(new XElement(Ns + "InitiateMultipartUploadResult",
                        new XElement(Ns + "Bucket", bucket), new XElement(Ns + "Key", key),
                        new XElement(Ns + "UploadId", id)));

                case "POST" when query.TryGetValue("uploadId", out var completeId):
                    if (!_uploads.Remove(completeId, out var completed))
                        return Error(HttpStatusCode.NotFound, "NoSuchUpload", "No such upload.");

                    var parts = XDocument.Parse(Encoding.UTF8.GetString(body)).Root!.Elements(Ns + "Part")
                        .Select(p => int.Parse(p.Element(Ns + "PartNumber")!.Value, CultureInfo.InvariantCulture))
                        .ToList();

                    if (!parts.SequenceEqual(completed.Parts.Keys))
                        return Error(HttpStatusCode.BadRequest, "InvalidPart", "Parts do not match.");

                    if (completed.Parts.Values.SkipLast(1).Any(p => p.Length < S3Connection.MinPartSize))
                        return Error(HttpStatusCode.BadRequest, "EntityTooSmall", "A part is too small.");

                    objects[key] = completed.Parts.Values.SelectMany(p => p).ToArray();

                    return Xml(new XElement(Ns + "CompleteMultipartUploadResult", new XElement(Ns + "Key", key)));

                case "DELETE" when query.TryGetValue("uploadId", out var abortId):
                    _uploads.Remove(abortId);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);

                case "DELETE":
                    objects.Remove(key);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);

                default:
                    return Error(HttpStatusCode.NotImplemented, "NotImplemented", method);
            }
        }
    }

    private HttpResponseMessage List(string bucket, SortedDictionary<string, byte[]> objects,
        Dictionary<string, string> query)
    {
        var prefix = query.GetValueOrDefault("prefix") ?? "";
        var delimiter = query.GetValueOrDefault("delimiter");
        var token = query.GetValueOrDefault("continuation-token");
        var maxKeys = Math.Min(PageSize,
            query.TryGetValue("max-keys", out var max) ? int.Parse(max, CultureInfo.InvariantCulture) : 1000);

        var contents = new List<XElement>();
        var prefixes = new List<string>();
        string? last = null;
        var truncated = false;

        foreach (var (key, data) in objects)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (token is not null)
            {
                var after = token[2..];

                if (string.CompareOrdinal(key, after) <= 0
                    || token[0] == 'P' && key.StartsWith(after, StringComparison.Ordinal))
                    continue;
            }

            string entry;

            if (delimiter is not null && key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal) is var at and >= 0)
            {
                var common = key[..(at + delimiter.Length)];

                if (prefixes.Contains(common))
                    continue;

                if (contents.Count + prefixes.Count == maxKeys)
                {
                    truncated = true;
                    break;
                }

                prefixes.Add(common);
                entry = "P:" + common;
            }
            else
            {
                if (contents.Count + prefixes.Count == maxKeys)
                {
                    truncated = true;
                    break;
                }

                contents.Add(new XElement(Ns + "Contents",
                    new XElement(Ns + "Key", key),
                    new XElement(Ns + "LastModified", "2026-10-27T12:00:00.000Z"),
                    new XElement(Ns + "ETag", ETag(data)),
                    new XElement(Ns + "Size", data.Length),
                    new XElement(Ns + "StorageClass", "STANDARD")));
                entry = "C:" + key;
            }

            last = entry;
        }

        return Xml(new XElement(Ns + "ListBucketResult",
            new XElement(Ns + "Name", bucket),
            new XElement(Ns + "Prefix", prefix),
            new XElement(Ns + "KeyCount", contents.Count + prefixes.Count),
            new XElement(Ns + "MaxKeys", maxKeys),
            new XElement(Ns + "IsTruncated", truncated ? "true" : "false"),
            truncated ? new XElement(Ns + "NextContinuationToken", last) : null,
            contents,
            prefixes.Select(p => new XElement(Ns + "CommonPrefixes", new XElement(Ns + "Prefix", p)))));
    }

    private static HttpResponseMessage DeleteMany(SortedDictionary<string, byte[]> objects, HttpRequestMessage request,
        byte[] body)
    {
        var md5 = request.Content!.Headers.TryGetValues("Content-MD5", out var values) ? values.Single() : null;

        if (md5 != Convert.ToBase64String(MD5.HashData(body)))
            return Error(HttpStatusCode.BadRequest, "InvalidDigest", "Content-MD5 is missing or wrong.");

        var keys = XDocument.Parse(Encoding.UTF8.GetString(body)).Root!.Elements(Ns + "Object")
            .Select(o => o.Element(Ns + "Key")!.Value)
            .ToList();

        if (keys.Count > 1000)
            return Error(HttpStatusCode.BadRequest, "MalformedXML", "Over 1000 keys.");

        foreach (var key in keys)
            objects.Remove(key);

        return Xml(new XElement(Ns + "DeleteResult"));
    }

    private byte[]? ReadCopySource(string copySource)
    {
        var source = Uri.UnescapeDataString(copySource.TrimStart('/'));
        var slash = source.IndexOf('/');

        return _buckets.TryGetValue(source[..slash], out var objects)
            ? objects.GetValueOrDefault(source[(slash + 1)..])
            : null;
    }

    private static bool SignatureMatches(HttpRequestMessage request, string host, string rawPath, string rawQuery,
        byte[] body)
    {
        if (!request.Headers.TryGetValues("Authorization", out var values))
            return false;

        var authorization = values.Single();
        var signedHeaders = authorization.Split("SignedHeaders=")[1].Split(',')[0].Split(';');
        var payloadHash = request.Headers.GetValues("x-amz-content-sha256").Single();

        if (payloadHash != S3Signer.UnsignedPayload && payloadHash != S3Signer.PayloadHash(body))
            return false;

        var headers = signedHeaders.Select(name => new KeyValuePair<string, string>(name, name switch
        {
            "host" => host,
            _ when request.Headers.TryGetValues(name, out var v) => string.Join(",", v),
            _ when request.Content?.Headers.TryGetValues(name, out var v) == true => string.Join(",", v),
            _ => ""
        })).ToList();

        var date = DateTime.ParseExact(request.Headers.GetValues("x-amz-date").Single(), "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        var query = rawQuery.Length == 0
            ? ""
            : S3Signer.CanonicalQuery(rawQuery.Split('&').Select(pair =>
            {
                var eq = pair.IndexOf('=');
                return eq < 0
                    ? new KeyValuePair<string, string>(Uri.UnescapeDataString(pair), "")
                    : new KeyValuePair<string, string>(Uri.UnescapeDataString(pair[..eq]),
                        Uri.UnescapeDataString(pair[(eq + 1)..]));
            }));

        // The raw query must already be in canonical form, or the server would see other bytes than were signed
        if (query != rawQuery)
            return false;

        var expected = S3Signer.Authorization(AccessKey, SecretKey, "us-east-1", "s3", date, request.Method.Method,
            rawPath, query, headers, payloadHash);

        return expected == authorization;
    }

    private static Dictionary<string, string> ParseQuery(string rawQuery) =>
        rawQuery.Length == 0
            ? []
            : rawQuery.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(
                p => Uri.UnescapeDataString(p[0]),
                p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

    private static string ETag(byte[] data) => $"\"{Convert.ToHexStringLower(MD5.HashData(data))}\"";

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };

    private static HttpResponseMessage Xml(XElement root) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new XDocument(new XDeclaration("1.0", "UTF-8", null), root).ToString(),
            Encoding.UTF8, "application/xml")
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) => new(status)
    {
        Content = new StringContent(
            new XElement("Error", new XElement("Code", code), new XElement("Message", message)).ToString(),
            Encoding.UTF8, "application/xml")
    };
}
