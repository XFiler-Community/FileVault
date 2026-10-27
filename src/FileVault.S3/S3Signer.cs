using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileVault.S3;

/// <summary>
/// AWS Signature Version 4 for the Authorization header. The canonical URI is encoded once, as S3 wants (other AWS
/// services encode it twice), so the caller passes the path exactly as it goes on the wire.
/// </summary>
internal static class S3Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string Service = "s3";

    /// <summary>SHA-256 of an empty body.</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    private const string HexUpper = "0123456789ABCDEF";

    /// <summary>
    /// RFC 3986 encoding of the UTF-8 bytes: only <c>A-Z a-z 0-9 - _ . ~</c> stay as they are, everything else is
    /// <c>%XX</c> with upper-case hex — a space is <c>%20</c>, never <c>+</c>.
    /// </summary>
    public static string UriEncode(string value, bool encodeSlash)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length * 3);

        foreach (var b in bytes)
        {
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                    or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~'
                || b == (byte)'/' && !encodeSlash)
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(HexUpper[b >> 4]).Append(HexUpper[b & 0xF]);
            }
        }

        return builder.ToString();
    }

    /// <summary>The query in canonical form: encoded names and values sorted by name, then value.</summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join('&', query
            .Select(p => (Name: UriEncode(p.Key, true), Value: UriEncode(p.Value, true)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}"));

    public static string CanonicalRequest(string method, string canonicalUri, string canonicalQuery,
        IEnumerable<KeyValuePair<string, string>> headers, string payloadHash, out string signedHeaders)
    {
        var sorted = headers
            .Select(h => (Name: h.Key.Trim().ToLowerInvariant(), Value: TrimValue(h.Value)))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();

        signedHeaders = string.Join(';', sorted.Select(h => h.Name));

        var builder = new StringBuilder();
        builder.Append(method).Append('\n')
            .Append(canonicalUri).Append('\n')
            .Append(canonicalQuery).Append('\n');

        foreach (var (name, value) in sorted)
            builder.Append(name).Append(':').Append(value).Append('\n');

        builder.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);
        return builder.ToString();
    }

    public static string Scope(DateTime utc, string region, string service) =>
        $"{utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{region}/{service}/aws4_request";

    public static string AmzDate(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string StringToSign(DateTime utc, string scope, string canonicalRequest) =>
        $"{Algorithm}\n{AmzDate(utc)}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

    public static byte[] SigningKey(string secretAccessKey, string date, string region, string service)
    {
        var key = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), date);
        key = HmacSha256(key, region);
        key = HmacSha256(key, service);
        return HmacSha256(key, "aws4_request");
    }

    /// <summary>The value of the Authorization header for a request whose signed headers are <paramref name="headers"/>.</summary>
    public static string Authorization(string accessKeyId, string secretAccessKey, string region, string service,
        DateTime utc, string method, string canonicalUri, string canonicalQuery,
        IEnumerable<KeyValuePair<string, string>> headers, string payloadHash)
    {
        var canonicalRequest = CanonicalRequest(method, canonicalUri, canonicalQuery, headers, payloadHash,
            out var signedHeaders);
        var scope = Scope(utc, region, service);
        var stringToSign = StringToSign(utc, scope, canonicalRequest);
        var key = SigningKey(secretAccessKey, utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture), region, service);
        var signature = Hex(HmacSha256(key, stringToSign));

        return $"{Algorithm} Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    public static string PayloadHash(ReadOnlySpan<byte> payload) => Hex(SHA256.HashData(payload));

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static byte[] HmacSha256(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    /// <summary>Header values are trimmed and inner runs of spaces collapse to one.</summary>
    private static string TrimValue(string value)
    {
        var trimmed = value.Trim();

        if (!trimmed.Contains("  ", StringComparison.Ordinal))
            return trimmed;

        var builder = new StringBuilder(trimmed.Length);
        var lastWasSpace = false;

        foreach (var c in trimmed)
        {
            if (c == ' ' && lastWasSpace)
                continue;

            builder.Append(c);
            lastWasSpace = c == ' ';
        }

        return builder.ToString();
    }
}
