using System.Net;

namespace FileVault.WebDav;

/// <summary>
/// A WebDAV request the server refused. <see cref="StatusCode"/> tells the cases apart: 401 — wrong user name or
/// password, 403 — no access, 404 — no such item, 423 — locked, 507 — no space left.
/// </summary>
public sealed class WebDavException(HttpStatusCode statusCode, string message) : IOException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
