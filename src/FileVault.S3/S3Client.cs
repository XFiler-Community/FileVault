using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FileVault.S3;

/// <summary>
/// The S3 REST calls for one bucket, signed with SigV4. Every response is checked, including the errors S3 sends
/// with a 200 (CopyObject, UploadPartCopy, CompleteMultipartUpload). XML goes through LINQ to XML, which is AOT-safe.
/// </summary>
internal sealed class S3Client(S3Connection connection, HttpClient http)
{
    private const int DeleteBatchSize = 1000;
    private const long CopyPartSize = 512L * 1024 * 1024;

    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly UriCreationOptions RawUri = new() { DangerousDisablePathAndQueryCanonicalization = true };

    // ── Reading ──────────────────────────────────────────────────────────────

    public async Task<S3ListPage> ListAsync(string prefix, string? delimiter, string? continuationToken,
        int? maxKeys, CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("prefix", prefix) };

        if (delimiter is not null)
            query.Add(new("delimiter", delimiter));
        if (continuationToken is not null)
            query.Add(new("continuation-token", continuationToken));
        if (maxKeys is { } max)
            query.Add(new("max-keys", max.ToString(CultureInfo.InvariantCulture)));

        using var response = await SendAsync(HttpMethod.Get, "", query, ct: ct).ConfigureAwait(false);
        var root = await ReadXmlAsync(response, ct).ConfigureAwait(false);

        var prefixes = Children(root, "CommonPrefixes")
            .Select(e => Child(e, "Prefix"))
            .OfType<string>()
            .ToList();

        var objects = Children(root, "Contents")
            .Select(e => new S3ObjectInfo(
                Child(e, "Key") ?? throw new InvalidDataException("S3 listed an object without a key."),
                long.TryParse(Child(e, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                DateTimeOffset.TryParse(Child(e, "LastModified"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.UnixEpoch,
                Child(e, "ETag")))
            .ToList();

        string? next = null;

        if (string.Equals(Child(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase))
            next = Child(root, "NextContinuationToken")
                   ?? throw new InvalidDataException("S3 cut the listing short but gave no continuation token.");

        return new S3ListPage(prefixes, objects, next);
    }

    /// <summary>Every object whose key starts with <paramref name="prefix"/>, page by page.</summary>
    public async IAsyncEnumerable<S3ObjectInfo> ListAllAsync(string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? token = null;

        do
        {
            var page = await ListAsync(prefix, null, token, null, ct).ConfigureAwait(false);

            foreach (var item in page.Objects)
                yield return item;

            token = page.NextContinuationToken;
        } while (token is not null);
    }

    /// <summary>
    /// The folder <paramref name="folderPrefix"/> (ending in '/') exists: its marker or a key below it. Asked of the
    /// parent's listing rather than the folder's own, so servers over a file system (rclone, MinIO in FS mode), which
    /// show an empty directory only as a common prefix of its parent, answer it too.
    /// </summary>
    public async Task<bool> FolderExistsAsync(string folderPrefix, CancellationToken ct)
    {
        var stem = folderPrefix[..^1];
        string? token = null;

        do
        {
            var page = await ListAsync(stem, "/", token, null, ct).ConfigureAwait(false);

            if (page.CommonPrefixes.Contains(folderPrefix) || page.Objects.Any(o => o.Key == folderPrefix))
                return true;

            token = page.NextContinuationToken;
        } while (token is not null);

        return false;
    }

    /// <summary>Null when there is no such object.</summary>
    public async Task<S3ObjectInfo?> HeadAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Head, key, ct: ct).ConfigureAwait(false);

            return new S3ObjectInfo(key,
                response.Content.Headers.ContentLength ?? 0,
                response.Content.Headers.LastModified ?? DateTimeOffset.UnixEpoch,
                Header(response, "ETag"));
        }
        catch (S3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>The object's content as it arrives; the caller disposes the stream.</summary>
    public async Task<(Stream stream, long totalBytes)> GetAsync(string key, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, key, completion: HttpCompletionOption.ResponseHeadersRead, ct: ct)
            .ConfigureAwait(false);

        try
        {
            var length = response.Content.Headers.ContentLength ?? -1;
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return (new S3ResponseStream(response, body, length), Math.Max(length, 0));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    public async Task PutAsync(string key, ReadOnlyMemory<byte> body, string? contentType, CancellationToken ct)
    {
        var content = new ReadOnlyMemoryContent(body);

        if (contentType is not null)
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await SendAsync(HttpMethod.Put, key, content: content,
            payloadHash: S3Signer.PayloadHash(body.Span), ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="key"/>: one streamed PUT when the length is known and up
    /// to <see cref="S3Connection.MultipartThreshold"/>, otherwise parts read into one reused buffer — a source that
    /// ends within the first part is one PUT too. A failed or cancelled multipart upload is aborted, so the old
    /// object, if any, stays as it was.
    /// </summary>
    public async Task UploadAsync(string key, Stream source, long length, IProgress<double>? progress,
        CancellationToken ct)
    {
        if (length > 0 && length <= connection.MultipartThreshold)
        {
            // The body streams, so it is not hashed up front; HTTPS and Content-Length keep it whole
            var content = new StreamContent(new S3ProgressStream(source, length, progress));
            content.Headers.ContentLength = length;

            using var response = await SendAsync(HttpMethod.Put, key, content: content,
                payloadHash: S3Signer.UnsignedPayload, ct: ct).ConfigureAwait(false);
            return;
        }

        var buffer = new byte[PartSizeFor(length)];
        var read = await ReadFullAsync(source, buffer, ct).ConfigureAwait(false);

        if (read < buffer.Length)
        {
            await PutAsync(key, buffer.AsMemory(0, read), null, ct).ConfigureAwait(false);
            return;
        }

        var uploadId = await CreateMultipartUploadAsync(key, ct).ConfigureAwait(false);

        try
        {
            var etags = new List<string>();
            long sent = 0;

            while (read > 0)
            {
                if (etags.Count == S3Connection.MaxParts)
                    throw new IOException($"'{key}' needs more than {S3Connection.MaxParts} parts; raise the part size.");

                etags.Add(await UploadPartAsync(key, uploadId, etags.Count + 1, buffer.AsMemory(0, read), ct)
                    .ConfigureAwait(false));

                sent += read;

                if (length > 0)
                    progress?.Report(Math.Min(1.0, (double)sent / length));

                read = read < buffer.Length ? 0 : await ReadFullAsync(source, buffer, ct).ConfigureAwait(false);
            }

            await CompleteMultipartUploadAsync(key, uploadId, etags, ct).ConfigureAwait(false);
        }
        catch
        {
            await AbortQuietlyAsync(key, uploadId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A server-side copy from <paramref name="source"/>'s bucket (the same account). Objects over
    /// <see cref="S3Connection.MultipartCopyThreshold"/> are copied in ranges; a server without UploadPartCopy gets
    /// the bytes streamed through this machine instead.
    /// </summary>
    public async Task CopyObjectAsync(S3Connection source, string sourceKey, long size, string key,
        IProgress<double>? progress, CancellationToken ct)
    {
        var threshold = Math.Min(connection.MultipartCopyThreshold, S3Connection.MaxCopyObjectSize);

        if (size <= threshold)
        {
            await CopyAsync(source.Bucket, sourceKey, key, ct).ConfigureAwait(false);
            return;
        }

        if (await TryMultipartCopyAsync(source.Bucket, sourceKey, size, key, threshold, progress, ct)
                .ConfigureAwait(false))
            return;

        var (stream, length) = await source.Client.GetAsync(sourceKey, ct).ConfigureAwait(false);

        await using (stream)
            await UploadAsync(key, stream, length, progress, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, key, ct: ct).ConfigureAwait(false);
        }
        catch (S3Exception e) when (e.StatusCode == HttpStatusCode.NotFound && e.Code != "NoSuchBucket")
        {
            // Already gone: what the caller wanted
        }
    }

    /// <summary>One DeleteObject for a single key, DeleteObjects in batches of 1000 for more.</summary>
    public async Task DeleteManyAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        if (keys.Count == 1)
        {
            await DeleteAsync(keys[0], ct).ConfigureAwait(false);
            return;
        }

        foreach (var batch in keys.Chunk(DeleteBatchSize))
        {
            var body = Xml(new XElement(Ns + "Delete",
                new XElement(Ns + "Quiet", "true"),
                batch.Select(k => new XElement(Ns + "Object", new XElement(Ns + "Key", k)))));

            // DeleteObjects must carry a checksum of its body
            var md5 = Convert.ToBase64String(MD5.HashData(body));

            using var response = await SendAsync(HttpMethod.Post, "", [new("delete", "")],
                new ByteArrayContent(body), S3Signer.PayloadHash(body), [new("content-md5", md5)], ct: ct)
                .ConfigureAwait(false);

            var root = await ReadXmlAsync(response, ct).ConfigureAwait(false);
            ThrowIfError(root, response.StatusCode);

            if (Children(root, "Error").FirstOrDefault() is { } error)
                throw new S3Exception(response.StatusCode, Child(error, "Code"),
                    $"S3 could not delete '{Child(error, "Key")}': {Child(error, "Code")} {Child(error, "Message")}");
        }
    }

    // ── Single calls ─────────────────────────────────────────────────────────

    internal async Task CopyAsync(string sourceBucket, string sourceKey, string key, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, key,
            headers: [new("x-amz-copy-source", CopySource(sourceBucket, sourceKey))], ct: ct).ConfigureAwait(false);

        ThrowIfError(await ReadXmlAsync(response, ct).ConfigureAwait(false), response.StatusCode);
    }

    internal async Task<string> CreateMultipartUploadAsync(string key, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, key, [new("uploads", "")], ct: ct).ConfigureAwait(false);
        var root = await ReadXmlAsync(response, ct).ConfigureAwait(false);

        return Child(root, "UploadId") ?? throw new InvalidDataException("S3 started an upload without an UploadId.");
    }

    internal async Task<string> UploadPartAsync(string key, string uploadId, int partNumber, ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, key, PartQuery(uploadId, partNumber),
            new ReadOnlyMemoryContent(body), S3Signer.PayloadHash(body.Span), ct: ct).ConfigureAwait(false);

        return Header(response, "ETag") ?? throw new InvalidDataException($"S3 gave no ETag for part {partNumber}.");
    }

    internal async Task<string> UploadPartCopyAsync(string key, string uploadId, int partNumber, string sourceBucket,
        string sourceKey, long first, long last, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, key, PartQuery(uploadId, partNumber), headers:
            [
                new("x-amz-copy-source", CopySource(sourceBucket, sourceKey)),
                new("x-amz-copy-source-range", $"bytes={first}-{last}")
            ], ct: ct).ConfigureAwait(false);

        var root = await ReadXmlAsync(response, ct).ConfigureAwait(false);
        ThrowIfError(root, response.StatusCode);

        // A server without UploadPartCopy may take the request for a plain UploadPart with an empty body
        // (rclone serve s3 does): no CopyPartResult means nothing was copied
        if (root is not { Name.LocalName: "CopyPartResult" } || Child(root, "ETag") is not { } etag)
            throw new S3Exception(HttpStatusCode.NotImplemented, "NotImplemented",
                "The server does not support UploadPartCopy.");

        return etag;
    }

    internal async Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<string> etags,
        CancellationToken ct)
    {
        var body = Xml(new XElement(Ns + "CompleteMultipartUpload",
            etags.Select((etag, i) => new XElement(Ns + "Part",
                new XElement(Ns + "PartNumber", i + 1),
                new XElement(Ns + "ETag", etag)))));

        using var response = await SendAsync(HttpMethod.Post, key, [new("uploadId", uploadId)],
            new ByteArrayContent(body), S3Signer.PayloadHash(body), ct: ct).ConfigureAwait(false);

        ThrowIfError(await ReadXmlAsync(response, ct).ConfigureAwait(false), response.StatusCode);
    }

    internal async Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, [new("uploadId", uploadId)], ct: ct)
            .ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<bool> TryMultipartCopyAsync(string sourceBucket, string sourceKey, long size, string key,
        long threshold, IProgress<double>? progress, CancellationToken ct)
    {
        var partSize = Math.Max(Math.Max(PartSizeFor(size), Math.Min(CopyPartSize, threshold)),
            (size + S3Connection.MaxParts - 1) / S3Connection.MaxParts);
        var uploadId = await CreateMultipartUploadAsync(key, ct).ConfigureAwait(false);

        try
        {
            var etags = new List<string>();

            for (long first = 0; first < size; first += partSize)
            {
                var last = Math.Min(first + partSize, size) - 1;
                etags.Add(await UploadPartCopyAsync(key, uploadId, etags.Count + 1, sourceBucket, sourceKey, first,
                    last, ct).ConfigureAwait(false));
                progress?.Report((double)(last + 1) / size);
            }

            await CompleteMultipartUploadAsync(key, uploadId, etags, ct).ConfigureAwait(false);
            return true;
        }
        catch (S3Exception e) when (e.StatusCode == HttpStatusCode.NotImplemented || e.Code == "NotImplemented")
        {
            await AbortQuietlyAsync(key, uploadId).ConfigureAwait(false);
            return false;
        }
        catch
        {
            await AbortQuietlyAsync(key, uploadId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Best effort with its own timeout: the caller's token is usually what was cancelled.</summary>
    private async Task AbortQuietlyAsync(string key, string uploadId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await AbortMultipartUploadAsync(key, uploadId, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // An upload left behind is cleaned by the bucket's lifecycle rules; the original error matters more
        }
    }

    private int PartSizeFor(long length)
    {
        var size = Math.Max(connection.PartSize, S3Connection.MinPartSize);

        if (length > 0)
            size = Math.Max(size, (length + S3Connection.MaxParts - 1) / S3Connection.MaxParts);

        return (int)Math.Min(size, Array.MaxLength);
    }

    private static async Task<int> ReadFullAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);

            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    private static List<KeyValuePair<string, string>> PartQuery(string uploadId, int partNumber) =>
        [new("partNumber", partNumber.ToString(CultureInfo.InvariantCulture)), new("uploadId", uploadId)];

    private static string CopySource(string bucket, string key) =>
        $"{S3Signer.UriEncode(bucket, true)}/{S3Signer.UriEncode(key, false)}";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string key,
        IReadOnlyList<KeyValuePair<string, string>>? query = null, HttpContent? content = null,
        string payloadHash = S3Signer.EmptyPayloadHash, IReadOnlyList<KeyValuePair<string, string>>? headers = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, CancellationToken ct = default)
    {
        var (url, canonicalUri, host) = Locate(key);
        var canonicalQuery = query is null ? "" : S3Signer.CanonicalQuery(query);

        if (canonicalQuery.Length > 0)
            url += "?" + canonicalQuery;

        var now = DateTime.UtcNow;
        var signed = new List<KeyValuePair<string, string>>
        {
            new("x-amz-content-sha256", payloadHash),
            new("x-amz-date", S3Signer.AmzDate(now))
        };

        if (connection.SessionToken is { } token)
            signed.Add(new("x-amz-security-token", token));
        if (headers is not null)
            signed.AddRange(headers);

        var authorization = S3Signer.Authorization(connection.AccessKeyId, connection.SecretAccessKey,
            connection.Region, S3Signer.Service, now, method.Method, canonicalUri, canonicalQuery,
            signed.Append(new("host", host)), payloadHash);

        using var request = new HttpRequestMessage(method, new Uri(url, RawUri));
        request.Content = content;
        request.Headers.Host = host;

        foreach (var (name, value) in signed)
        {
            if (name == "content-md5")
                content!.Headers.TryAddWithoutValidation("Content-MD5", value);
            else
                request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        var response = await http.SendAsync(request, completion, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
            throw await ErrorAsync(response, method, key, ct).ConfigureAwait(false);
    }

    /// <summary>The URL of a key (empty — the bucket), the same path in canonical form, and the Host it goes to.</summary>
    private (string Url, string CanonicalUri, string Host) Locate(string key)
    {
        var endpoint = connection.Endpoint;
        var basePath = endpoint.AbsolutePath.TrimEnd('/');
        var encodedKey = S3Signer.UriEncode(key, encodeSlash: false);

        string host, path;

        if (connection.ForcePathStyle)
        {
            host = endpoint.Authority;
            path = $"{basePath}/{S3Signer.UriEncode(connection.Bucket, true)}" + (key.Length > 0 ? "/" + encodedKey : "");
        }
        else
        {
            host = $"{connection.Bucket}.{endpoint.Authority}";
            path = $"{basePath}/{encodedKey}";
        }

        return ($"{endpoint.Scheme}://{host}{path}", path, host);
    }

    private static async Task<S3Exception> ErrorAsync(HttpResponseMessage response, HttpMethod method, string key,
        CancellationToken ct)
    {
        string? code = null, message = null;

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (body.Length > 0 && XDocument.Parse(body).Root is { } root)
            {
                code = Child(root, "Code");
                message = Child(root, "Message");
            }
        }
        catch (XmlException)
        {
            // Not S3's XML (a proxy's page): the status says enough
        }

        var target = key.Length == 0 ? "the bucket" : $"'{key}'";
        return new S3Exception(response.StatusCode, code,
            $"S3 {method} {target}: {(int)response.StatusCode} {code ?? response.ReasonPhrase}" +
            (message is null ? "" : $" — {message}"));
    }

    private static void ThrowIfError(XElement? root, HttpStatusCode status)
    {
        if (root is { Name.LocalName: "Error" })
            throw new S3Exception(status, Child(root, "Code"), $"S3: {Child(root, "Code")} — {Child(root, "Message")}");
    }

    private static async Task<XElement?> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Whitespace is kept: a key may be a single space
        return string.IsNullOrWhiteSpace(body) ? null : XDocument.Parse(body, LoadOptions.PreserveWhitespace).Root;
    }

    private static IEnumerable<XElement> Children(XElement? element, string name) =>
        element?.Elements().Where(e => e.Name.LocalName == name) ?? [];

    private static string? Child(XElement? element, string name) => Children(element, name).FirstOrDefault()?.Value;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static byte[] Xml(XElement element) =>
        Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting));
}
