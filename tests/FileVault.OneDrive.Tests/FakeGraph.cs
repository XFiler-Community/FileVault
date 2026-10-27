using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace FileVault.OneDrive.Tests;

/// <summary>
/// An in-memory OneDrive behind the Microsoft Graph endpoints <see cref="OneDriveClient"/> calls: items by id and path,
/// paged children, simple uploads and upload sessions (pre-authenticated upload URLs), downloads and thumbnails that
/// redirect to storage, async copy with a monitor URL, PATCH moves, recycle bin and permanent delete, search, sharing
/// links. Names are case-insensitive, as in OneDrive.
/// </summary>
internal sealed class FakeGraph : HttpMessageHandler
{
    public const string RootId = "ROOT!101";
    public const string DriveId = "drive-1";
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    public sealed class Node
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public string? ParentId { get; set; }
        public bool IsFolder { get; init; }
        public byte[] Content { get; set; } = [];
        public DateTimeOffset Modified { get; set; } = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        public bool Recycled { get; set; }
        public List<(string Id, string Type, string Scope, string Url)> Links { get; } = [];
    }

    private sealed class UploadSession
    {
        public required string ParentId { get; init; }
        public required string Name { get; init; }
        public required string Conflict { get; init; }
        public MemoryStream Received { get; } = new();
    }

    private sealed record CopyJob(string? ResourceId, string? ErrorCode)
    {
        public int Polls { get; set; }
    }

    private readonly Dictionary<string, Node> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UploadSession> _sessions = [];
    private readonly Dictionary<string, CopyJob> _jobs = [];
    private readonly Lock _gate = new();
    private int _nextId = 1000;

    public FakeGraph()
    {
        _items[RootId] = new Node { Id = RootId, Name = "root", IsFolder = true };
    }

    public string AccessToken { get; set; } = "token-1";
    public string DriveType { get; set; } = "personal";
    public long QuotaTotal { get; set; } = 5L * 1024 * 1024 * 1024;
    public bool PermanentDeleteSupported { get; set; } = true;

    /// <summary>Search answers matches from the whole drive, as a folder search might; the client must filter.</summary>
    public bool SearchWholeDrive { get; set; } = true;

    /// <summary>Search results carry <c>parentReference.path</c> (OneDrive personal does, business may not).</summary>
    public bool SearchIncludesPath { get; set; }

    /// <summary>Copy monitors answer <c>inProgress</c> this many times before <c>completed</c>.</summary>
    public int CopyPollsBeforeDone { get; set; } = 2;

    /// <summary>Custom thumbnail sizes are refused with 400, as a drive without them would.</summary>
    public bool RefuseCustomThumbnailSizes { get; set; }

    /// <summary>Answers for the next Graph API calls (not storage hosts), before routing: throttling and so on.</summary>
    public Queue<(HttpStatusCode Status, TimeSpan? RetryAfter)> GraphFailures { get; } = new();

    /// <summary>Answers for the next upload-fragment PUTs.</summary>
    public Queue<HttpStatusCode> FragmentFailures { get; } = new();

    /// <summary>Every request as "METHOD url" with the Authorization it carried.</summary>
    public List<(string Request, string? Authorization)> Requests { get; } = [];

    /// <summary>Content-Range of every accepted upload fragment: (from, to, total).</summary>
    public List<(long From, long To, long Total)> Fragments { get; } = [];

    public int UploadSessionsCreated { get; private set; }
    public int UploadSessionsCancelled { get; private set; }

    public OneDriveClient CreateClient(OneDriveClientOptions? options = null, List<TimeSpan>? delays = null,
        Func<bool, string>? token = null)
    {
        var client = new OneDriveClient(new HttpClient(this), (force, _) =>
            ValueTask.FromResult(token?.Invoke(force) ?? AccessToken), options);
        client.Delay = (delay, _) =>
        {
            delays?.Add(delay);
            return Task.CompletedTask;
        };
        return client;
    }

    // ── Seeding and inspecting ──────────────────────────────────────────────

    public string AddFolder(string parentId, string name)
    {
        lock (_gate)
            return Add(parentId, name, true, []).Id;
    }

    public string AddFile(string parentId, string name, byte[] content)
    {
        lock (_gate)
            return Add(parentId, name, false, content).Id;
    }

    public Node? Find(string parentId, string name)
    {
        lock (_gate)
            return Child(parentId, name);
    }

    public Node Get(string id)
    {
        lock (_gate)
            return _items[id];
    }

    public IReadOnlyList<Node> Children(string parentId)
    {
        lock (_gate)
            return _items.Values.Where(n => n.ParentId == parentId && !n.Recycled).ToList();
    }

    public bool Exists(string id)
    {
        lock (_gate)
            return _items.TryGetValue(id, out var node) && !node.Recycled;
    }

    private Node Add(string parentId, string name, bool folder, byte[] content)
    {
        var node = new Node { Id = $"ITEM!{_nextId++}", Name = name, ParentId = parentId, IsFolder = folder, Content = content };
        _items[node.Id] = node;
        return node;
    }

    private Node? Child(string parentId, string name) =>
        _items.Values.FirstOrDefault(n => n.ParentId == parentId && !n.Recycled
                                          && n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private string PathOf(Node node)
    {
        var names = new List<string>();
        for (var n = node; n.ParentId is not null; n = _items[n.ParentId])
            names.Insert(0, n.Name);
        return "/drive/root:" + (names.Count == 0 ? "" : "/" + string.Join('/', names));
    }

    private bool IsBelow(Node node, string ancestorId)
    {
        for (var n = node; n.ParentId is not null; n = _items[n.ParentId])
            if (n.ParentId == ancestorId)
                return true;
        return false;
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);

        lock (_gate)
        {
            var authorization = request.Headers.Authorization?.ToString();
            Requests.Add(($"{request.Method} {request.RequestUri}", authorization));

            var uri = request.RequestUri!;

            return uri.Host switch
            {
                "graph.microsoft.com" => RouteGraph(request, body ?? [], authorization),
                "upload.fake" => RouteUpload(request, body ?? []),
                "download.fake" => Bytes(_items[uri.Segments[^1]].Content),
                "thumbs.fake" => Bytes(Encoding.UTF8.GetBytes($"thumbnail {Uri.UnescapeDataString(uri.Segments[^1])}")),
                "monitor.fake" => RouteMonitor(uri.Segments[^1]),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }
    }

    private HttpResponseMessage RouteGraph(HttpRequestMessage request, byte[] body, string? authorization)
    {
        if (authorization != $"Bearer {AccessToken}")
            return Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "Access token has expired.");

        if (GraphFailures.TryDequeue(out var failure))
        {
            var response = Error(failure.Status, "activityLimitReached", "Throttled");
            if (failure.RetryAfter is { } after)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(after);
            return response;
        }

        var uri = request.RequestUri!;
        var path = uri.AbsolutePath["/v1.0/".Length..];
        var query = HttpUtility.ParseQueryString(uri.Query);
        var method = request.Method.Method;

        if (path == "me/drive")
            return Json(new JsonObject
            {
                ["id"] = DriveId,
                ["driveType"] = DriveType,
                ["owner"] = new JsonObject { ["user"] = new JsonObject { ["displayName"] = "Anna" } },
                ["quota"] = new JsonObject
                {
                    ["total"] = QuotaTotal,
                    ["used"] = _items.Values.Where(n => !n.Recycled).Sum(n => (long)n.Content.Length),
                    ["remaining"] = QuotaTotal - _items.Values.Where(n => !n.Recycled).Sum(n => (long)n.Content.Length)
                }
            });

        string id;
        string rest;

        if (path.StartsWith("me/drive/root", StringComparison.Ordinal))
        {
            id = RootId;
            rest = path["me/drive/root".Length..];
        }
        else if (path.StartsWith("me/drive/items/", StringComparison.Ordinal))
        {
            var after = path["me/drive/items/".Length..];
            var end = after.IndexOfAny(['/', ':']);
            id = Uri.UnescapeDataString(end < 0 ? after : after[..end]);
            rest = end < 0 ? "" : after[end..];
        }
        else
            return Error(HttpStatusCode.BadRequest, "invalidRequest", $"Unknown path {path}");

        if (id == "root")
            id = RootId;

        if (!_items.TryGetValue(id, out var node) || node.Recycled)
            return Error(HttpStatusCode.NotFound, "itemNotFound", "Item not found");

        // Path addressing: ":/a/b" or ":/a/b:/action"
        string? pendingName = null;

        if (rest.StartsWith(":/", StringComparison.Ordinal))
        {
            var addressed = rest[2..];
            var colon = addressed.IndexOf(':');
            rest = colon < 0 ? "" : addressed[(colon + 1)..];
            var segments = (colon < 0 ? addressed : addressed[..colon]).Split('/').Select(Uri.UnescapeDataString).ToList();

            for (var i = 0; i < segments.Count; i++)
            {
                var child = Child(node.Id, segments[i]);

                if (child is null)
                {
                    if (i == segments.Count - 1 && rest is "/content" or "/createUploadSession")
                    {
                        pendingName = segments[i];
                        break;
                    }

                    return Error(HttpStatusCode.NotFound, "itemNotFound", "Item not found");
                }

                node = child;
            }

            if (pendingName is null && rest is "/content" or "/createUploadSession" && method != "GET")
            {
                pendingName = node.Name;
                node = _items[node.ParentId!];
            }
        }

        var action = Uri.UnescapeDataString(rest);

        switch (method, action)
        {
            case ("GET", ""):
                return Json(ItemJson(node));

            case ("PATCH", ""):
                return Patch(node, JsonNode.Parse(body)!.AsObject());

            case ("DELETE", ""):
                node.Recycled = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            case ("POST", "/permanentDelete"):
                if (!PermanentDeleteSupported)
                    return Error(HttpStatusCode.BadRequest, "notSupported", "Not supported for this drive.");
                RemoveTree(node);
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            case ("GET", "/children"):
                return Page(_items.Values.Where(n => n.ParentId == node.Id && !n.Recycled).OrderBy(n => n.Id).ToList(),
                    query, $"{Graph}me/drive/items/{Uri.EscapeDataString(node.Id)}/children");

            case ("POST", "/children"):
                return CreateFolder(node, JsonNode.Parse(body)!.AsObject());

            case ("GET", "/content"):
                return Redirect($"https://download.fake/{node.Id}");

            case ("PUT", "/content"):
                return PutContent(node, pendingName!, query["@microsoft.graph.conflictBehavior"] ?? "replace", body);

            case ("POST", "/createUploadSession"):
                return CreateUploadSession(node, pendingName!, JsonNode.Parse(body)!.AsObject());

            case ("POST", "/copy"):
                return Copy(node, JsonNode.Parse(body)!.AsObject());

            case ("GET", "/permissions"):
                return Json(new JsonObject
                {
                    ["value"] = new JsonArray(node.Links.Select(l => (JsonNode)new JsonObject
                    {
                        ["id"] = l.Id,
                        ["roles"] = new JsonArray("read"),
                        ["link"] = new JsonObject { ["type"] = l.Type, ["scope"] = l.Scope, ["webUrl"] = l.Url }
                    }).ToArray())
                });

            case ("POST", "/createLink"):
                return CreateLink(node, JsonNode.Parse(body)!.AsObject());
        }

        if (method == "DELETE" && action.StartsWith("/permissions/", StringComparison.Ordinal))
        {
            var removed = node.Links.RemoveAll(l => l.Id == action["/permissions/".Length..]);
            return removed > 0
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Error(HttpStatusCode.NotFound, "itemNotFound", "No such permission");
        }

        if (method == "GET" && action.StartsWith("/thumbnails/0/", StringComparison.Ordinal) && action.EndsWith("/content"))
        {
            var size = action["/thumbnails/0/".Length..^"/content".Length];

            if (RefuseCustomThumbnailSizes && size.StartsWith('c'))
                return Error(HttpStatusCode.BadRequest, "invalidRequest", "Custom sizes are not supported");

            return IsPicture(node)
                ? Redirect($"https://thumbs.fake/{Uri.EscapeDataString(node.Id)}/{size}")
                : Error(HttpStatusCode.NotFound, "itemNotFound", "No thumbnail");
        }

        if (method == "GET" && action.StartsWith("/search(q='", StringComparison.Ordinal) && action.EndsWith("')"))
        {
            var q = action["/search(q='".Length..^2].Replace("''", "'");
            var scope = SearchWholeDrive ? RootId : node.Id;
            var hits = _items.Values
                .Where(n => !n.Recycled && n.Id != RootId && IsBelow(n, scope)
                            && n.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Id).ToList();
            return Page(hits, query, $"{Graph}me/drive/items/{Uri.EscapeDataString(node.Id)}/search(q='{Uri.EscapeDataString(q)}')",
                SearchIncludesPath);
        }

        return Error(HttpStatusCode.BadRequest, "invalidRequest", $"Unknown action {method} {action}");
    }

    private static bool IsPicture(Node node) =>
        !node.IsFolder && Path.GetExtension(node.Name).ToLowerInvariant() is ".jpg" or ".png";

    private void RemoveTree(Node node)
    {
        foreach (var child in _items.Values.Where(n => n.ParentId == node.Id).ToList())
            RemoveTree(child);
        _items.Remove(node.Id);
    }

    private HttpResponseMessage Page(List<Node> all, System.Collections.Specialized.NameValueCollection query,
        string baseUrl, bool withPath = true)
    {
        var top = int.TryParse(query["$top"], out var t) ? t : 200;
        var skip = int.TryParse(query["$skiptoken"], out var s) ? s : 0;
        var page = all.Skip(skip).Take(top).ToList();

        var json = new JsonObject { ["value"] = new JsonArray(page.Select(n => (JsonNode)ItemJson(n, withPath)).ToArray()) };

        if (skip + top < all.Count)
            json["@odata.nextLink"] = $"{baseUrl}?$top={top}&$skiptoken={skip + top}";

        return Json(json);
    }

    private JsonObject ItemJson(Node node, bool withPath = true)
    {
        var json = new JsonObject
        {
            ["id"] = node.Id,
            ["name"] = node.Name,
            ["size"] = node.IsFolder ? 0 : node.Content.Length,
            ["lastModifiedDateTime"] = node.Modified.ToString("O")
        };

        if (node.ParentId is { } parentId)
        {
            var parentReference = new JsonObject { ["id"] = parentId, ["driveId"] = DriveId, ["driveType"] = DriveType };
            if (withPath)
                parentReference["path"] = PathOf(_items[parentId]);
            json["parentReference"] = parentReference;
        }
        else
        {
            json["root"] = new JsonObject();
            json["parentReference"] = new JsonObject { ["driveId"] = DriveId, ["driveType"] = DriveType };
        }

        if (node.IsFolder)
            json["folder"] = new JsonObject { ["childCount"] = _items.Values.Count(n => n.ParentId == node.Id && !n.Recycled) };
        else
            json["file"] = new JsonObject { ["mimeType"] = "application/octet-stream" };

        if (IsPicture(node))
            json["image"] = new JsonObject();

        return json;
    }

    private HttpResponseMessage CreateFolder(Node parent, JsonObject body)
    {
        var name = body["name"]!.GetValue<string>();
        var conflict = body["@microsoft.graph.conflictBehavior"]?.GetValue<string>() ?? "fail";

        if (Child(parent.Id, name) is not null && conflict == "fail")
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "Name already exists");

        return Json(ItemJson(Add(parent.Id, name, true, [])), HttpStatusCode.Created);
    }

    private HttpResponseMessage PutContent(Node parent, string name, string conflict, byte[] body) =>
        Commit(parent.Id, name, conflict, body) is { } node
            ? Json(ItemJson(node), HttpStatusCode.Created)
            : Error(HttpStatusCode.Conflict, "nameAlreadyExists", "Name already exists");

    /// <summary>Writes a finished upload; null when the name is taken and the conflict behaviour is "fail".</summary>
    private Node? Commit(string parentId, string name, string conflict, byte[] content)
    {
        var existing = Child(parentId, name);

        if (existing is null)
            return Add(parentId, name, false, content);

        if (conflict == "fail" || existing.IsFolder)
            return null;

        existing.Content = content;
        existing.Modified = existing.Modified.AddMinutes(1);
        return existing;
    }

    private HttpResponseMessage CreateUploadSession(Node parent, string name, JsonObject body)
    {
        var item = body["item"]!.AsObject();
        var sessionId = Guid.NewGuid().ToString("N");

        _sessions[sessionId] = new UploadSession
        {
            ParentId = parent.Id,
            Name = name,
            Conflict = item["@microsoft.graph.conflictBehavior"]?.GetValue<string>() ?? "fail"
        };
        UploadSessionsCreated++;

        return Json(new JsonObject
        {
            ["uploadUrl"] = $"https://upload.fake/up/{sessionId}",
            ["expirationDateTime"] = "2026-10-28T09:00:00Z"
        });
    }

    private HttpResponseMessage RouteUpload(HttpRequestMessage request, byte[] body)
    {
        var sessionId = request.RequestUri!.Segments[^1];

        if (!_sessions.TryGetValue(sessionId, out var session))
            return Error(HttpStatusCode.NotFound, "itemNotFound", "Upload session not found");

        if (request.Headers.Authorization is not null)
            return Error(HttpStatusCode.Unauthorized, "unauthenticated", "Upload URLs take no Authorization header");

        switch (request.Method.Method)
        {
            case "DELETE":
                _sessions.Remove(sessionId);
                UploadSessionsCancelled++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            case "GET":
                return Json(new JsonObject { ["nextExpectedRanges"] = new JsonArray($"{session.Received.Length}-") });
        }

        if (FragmentFailures.TryDequeue(out var failure))
            return Error(failure, "serviceNotAvailable", "Try again");

        var range = request.Content!.Headers.ContentRange!;
        var from = range.From!.Value;
        var to = range.To!.Value;
        var total = range.Length!.Value;

        if (from != session.Received.Length)
            return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "invalidRange", "Range already received");

        if (to - from + 1 != body.Length)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "Content-Range does not match the body");

        if (to != total - 1 && body.Length % OneDriveClientOptions.UploadFragmentUnit != 0)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "Fragment is not a multiple of 320 KiB");

        Fragments.Add((from, to, total));
        session.Received.Write(body);

        if (to != total - 1)
            return Json(new JsonObject
            {
                ["expirationDateTime"] = "2026-10-28T09:00:00Z",
                ["nextExpectedRanges"] = new JsonArray($"{to + 1}-")
            }, HttpStatusCode.Accepted);

        _sessions.Remove(sessionId);

        return Commit(session.ParentId, session.Name, session.Conflict, session.Received.ToArray()) is { } node
            ? Json(ItemJson(node), HttpStatusCode.Created)
            : Error(HttpStatusCode.Conflict, "nameAlreadyExists", "Name already exists");
    }

    private HttpResponseMessage Patch(Node node, JsonObject body)
    {
        var name = body["name"]?.GetValue<string>() ?? node.Name;
        var parentId = body["parentReference"]?["id"]?.GetValue<string>() ?? node.ParentId!;

        if (!_items.ContainsKey(parentId))
            return Error(HttpStatusCode.NotFound, "itemNotFound", "Parent not found");

        if (parentId == node.Id || IsBelow(_items[parentId], node.Id))
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "Cannot move a folder into itself");

        if (Child(parentId, name) is { } other && other.Id != node.Id)
            return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "Name already exists");

        node.Name = name;
        node.ParentId = parentId;
        return Json(ItemJson(node));
    }

    private HttpResponseMessage Copy(Node source, JsonObject body)
    {
        var parentId = body["parentReference"]?["id"]?.GetValue<string>() ?? source.ParentId!;
        var name = body["name"]?.GetValue<string>() ?? source.Name;
        var jobId = Guid.NewGuid().ToString("N");

        if (body["parentReference"]?["driveId"]?.GetValue<string>() is { } driveId && driveId != DriveId)
            return Error(HttpStatusCode.BadRequest, "invalidRequest", "Unknown drive");

        _jobs[jobId] = Child(parentId, name) is not null
            ? new CopyJob(null, "nameAlreadyExists")
            : new CopyJob(CopyTree(source, parentId, name).Id, null);

        var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        response.Headers.Location = new Uri($"https://monitor.fake/monitor/{jobId}");
        return response;
    }

    private Node CopyTree(Node source, string parentId, string name)
    {
        var copy = Add(parentId, name, source.IsFolder, source.Content.ToArray());

        foreach (var child in _items.Values.Where(n => n.ParentId == source.Id && !n.Recycled).ToList())
            CopyTree(child, copy.Id, child.Name);

        return copy;
    }

    private HttpResponseMessage RouteMonitor(string jobId)
    {
        var job = _jobs[jobId];

        if (job.ErrorCode is { } code)
            return Json(new JsonObject
            {
                ["status"] = "failed",
                ["error"] = new JsonObject { ["code"] = code, ["message"] = "Name already exists" }
            });

        if (job.Polls++ < CopyPollsBeforeDone)
            return Json(new JsonObject
            {
                ["operation"] = "ItemCopy",
                ["status"] = "inProgress",
                ["percentageComplete"] = 50.0
            }, HttpStatusCode.Accepted);

        return Json(new JsonObject
        {
            ["status"] = "completed",
            ["percentageComplete"] = 100.0,
            ["resourceId"] = job.ResourceId
        }, HttpStatusCode.Accepted);
    }

    private HttpResponseMessage CreateLink(Node node, JsonObject body)
    {
        var type = body["type"]!.GetValue<string>();
        var scope = body["scope"]!.GetValue<string>();

        if (node.Links.FirstOrDefault(l => l.Type == type && l.Scope == scope) is { Id: not null } existing)
            return Json(LinkJson(existing));

        var link = ($"perm-{_nextId++}", type, scope, $"https://1drv.ms/u/s!{node.Id}");
        node.Links.Add(link);
        return Json(LinkJson(link), HttpStatusCode.Created);
    }

    private static JsonObject LinkJson((string Id, string Type, string Scope, string Url) link) => new()
    {
        ["id"] = link.Id,
        ["roles"] = new JsonArray("read"),
        ["link"] = new JsonObject { ["type"] = link.Type, ["scope"] = link.Scope, ["webUrl"] = link.Url }
    };

    // ── Answers ─────────────────────────────────────────────────────────────

    private static HttpResponseMessage Json(JsonNode json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(content)
    };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);
}
