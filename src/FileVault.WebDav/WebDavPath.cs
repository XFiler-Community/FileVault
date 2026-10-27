using System.Text;

namespace FileVault.WebDav;

/// <summary>
/// Server paths: decoded (a space is a space, not <c>%20</c>), absolute from the host root, no trailing slash; the
/// host root itself is the empty string. They are percent-encoded only when a request URL is built.
/// </summary>
internal static class WebDavPath
{
    public static string Combine(string parent, string name) => parent + "/" + name;

    public static string Parent(string path) => path[..Math.Max(path.LastIndexOf('/'), 0)];

    public static string GetName(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static bool IsSameOrInside(string path, string folder) =>
        path == folder || path.StartsWith(folder + "/", StringComparison.Ordinal);

    public static void EnsureValidName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (name.Contains('/') || name is "." or "..")
            throw new ArgumentException($"'{name}' is not a valid file or folder name.", nameof(name));
    }

    /// <summary>A decoded path with any slashes in any places, made canonical; <c>.</c> and <c>..</c> are refused.</summary>
    public static string Normalize(string path)
    {
        var builder = new StringBuilder(path.Length + 1);

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                throw new ArgumentException($"'{path}' must not contain '.' or '..' segments.", nameof(path));

            builder.Append('/').Append(segment);
        }

        return builder.ToString();
    }

    /// <summary>Every segment percent-encoded as UTF-8: spaces, <c>#</c>, <c>%</c>, <c>?</c> and non-ASCII letters included.</summary>
    public static string Encode(string path)
    {
        var builder = new StringBuilder(path.Length * 2);

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            builder.Append('/').Append(Uri.EscapeDataString(segment));

        return builder.ToString();
    }

    /// <summary>
    /// The decoded path of a multistatus <c>href</c>, which servers send as an absolute URL, an absolute path or
    /// (rarely) a path relative to the request. The path is taken as text, not through <see cref="Uri"/>, so a
    /// server that leaves <c>#</c> or <c>?</c> unencoded still names the right file.
    /// </summary>
    public static string DecodeHref(string href, Uri requestUri)
    {
        href = href.Trim();
        string raw;

        var scheme = href.IndexOf("://", StringComparison.Ordinal);

        if (scheme >= 0)
        {
            var slash = href.IndexOf('/', scheme + 3);
            raw = slash < 0 ? "/" : href[slash..];
        }
        else if (href.StartsWith('/'))
            raw = href;
        else
            raw = new Uri(requestUri, href).AbsolutePath;

        var builder = new StringBuilder(raw.Length);

        foreach (var segment in Uri.UnescapeDataString(raw).Split('/', StringSplitOptions.RemoveEmptyEntries))
            builder.Append('/').Append(segment);

        return builder.ToString();
    }
}
