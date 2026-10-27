using System.Net;

namespace FileVault.S3;

/// <summary>
/// An S3 call failed. <see cref="StatusCode"/> and <see cref="Code"/> (S3's error code, e.g. <c>AccessDenied</c>,
/// <c>NoSuchBucket</c>, <c>SignatureDoesNotMatch</c>) tell why.
/// </summary>
public sealed class S3Exception(HttpStatusCode statusCode, string? code, string message) : IOException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}

internal sealed record S3ObjectInfo(string Key, long Size, DateTimeOffset LastModified, string? ETag);

internal sealed record S3ListPage(
    IReadOnlyList<string> CommonPrefixes,
    IReadOnlyList<S3ObjectInfo> Objects,
    string? NextContinuationToken);
