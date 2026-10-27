using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace FileVault.WebDav;

/// <summary>One <c>response</c> of a PROPFIND answer. <see cref="Path"/> is the decoded path of its href.</summary>
internal sealed record WebDavResource(
    string Path,
    bool IsCollection,
    long? Length,
    DateTimeOffset? LastModified,
    long? QuotaUsedBytes,
    long? QuotaAvailableBytes);

/// <summary>
/// Reads a <c>207 Multi-Status</c> body. Servers differ in prefixes (Nextcloud <c>d:</c>, IIS and rclone <c>D:</c>,
/// some a default namespace) and some put DAV properties in no namespace at all, so elements are matched by local
/// name in the <c>DAV:</c> namespace or none. Only properties of a <c>propstat</c> with a 2xx status are read:
/// properties the server does not have come back under 404.
/// </summary>
internal static class WebDavMultiStatus
{
    private const string DavNamespace = "DAV:";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true
    };

    public static async Task<IReadOnlyList<WebDavResource>> ParseAsync(Stream body, Uri requestUri, CancellationToken ct)
    {
        using var reader = XmlReader.Create(body, ReaderSettings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
        return Parse(document, requestUri);
    }

    public static IReadOnlyList<WebDavResource> Parse(string xml, Uri requestUri)
    {
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
        return Parse(XDocument.Load(reader), requestUri);
    }

    private static List<WebDavResource> Parse(XDocument document, Uri requestUri)
    {
        var root = document.Root;

        if (root is null || !IsDav(root, "multistatus"))
            throw new WebDavException(System.Net.HttpStatusCode.MultiStatus,
                "The server's PROPFIND answer is not a WebDAV multistatus document.");

        var resources = new List<WebDavResource>();

        foreach (var response in root.Elements().Where(e => IsDav(e, "response")))
        {
            var href = Child(response, "href")?.Value;

            if (string.IsNullOrWhiteSpace(href))
                continue;

            if (Child(response, "status") is { } status && !IsSuccessStatus(status.Value))
                continue;

            var props = new Dictionary<string, XElement>(StringComparer.Ordinal);

            foreach (var propstat in response.Elements().Where(e => IsDav(e, "propstat")))
            {
                if (Child(propstat, "status") is { } propStatus && !IsSuccessStatus(propStatus.Value))
                    continue;

                if (Child(propstat, "prop") is not { } prop)
                    continue;

                foreach (var property in prop.Elements().Where(e => e.Name.NamespaceName is DavNamespace or ""))
                    props.TryAdd(property.Name.LocalName, property);
            }

            var isCollection = props.TryGetValue("resourcetype", out var type) && Child(type, "collection") is not null;

            resources.Add(new WebDavResource(
                WebDavPath.DecodeHref(href, requestUri),
                isCollection,
                ParseLong(props, "getcontentlength"),
                ParseDate(props, "getlastmodified") ?? ParseDate(props, "creationdate"),
                ParseLong(props, "quota-used-bytes"),
                ParseLong(props, "quota-available-bytes")));
        }

        return resources;
    }

    private static bool IsDav(XElement element, string localName) =>
        element.Name.LocalName == localName && element.Name.NamespaceName is DavNamespace or "";

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => IsDav(e, localName));

    /// <summary><c>HTTP/1.1 200 OK</c> and the like.</summary>
    private static bool IsSuccessStatus(string status)
    {
        var parts = status.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[1].StartsWith('2');
    }

    private static long? ParseLong(Dictionary<string, XElement> props, string name) =>
        props.TryGetValue(name, out var element)
        && long.TryParse(element.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary><c>getlastmodified</c> is RFC 1123 (<c>Tue, 27 Oct 2026 15:17:18 GMT</c>), <c>creationdate</c> ISO 8601.</summary>
    private static DateTimeOffset? ParseDate(Dictionary<string, XElement> props, string name)
    {
        if (!props.TryGetValue(name, out var element))
            return null;

        var text = element.Value.Trim();

        if (DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                out var date)
            || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
            return date;

        return null;
    }
}
