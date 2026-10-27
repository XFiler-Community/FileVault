using System.Net;

namespace FileVault.OneDrive;

/// <summary>
/// A Microsoft Graph call failed. <see cref="Code"/> is Graph's error code (<c>nameAlreadyExists</c>,
/// <c>itemNotFound</c>, <c>accessDenied</c>, <c>quotaLimitReached</c>…) when it gave one.
/// </summary>
public sealed class OneDriveApiException(HttpStatusCode statusCode, string? code, string message) : IOException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;

    internal bool IsNameConflict => Code == "nameAlreadyExists" || StatusCode == HttpStatusCode.Conflict;
}
