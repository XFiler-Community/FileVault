using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FileVault.Dropbox.Tests;

/// <summary>
/// An in-memory Dropbox behind the HTTP API v2 routes <see cref="DropboxClient"/> and <see cref="DropboxAuth"/> call:
/// a case-insensitive tree of files and folders, upload sessions, batch jobs, search, shared links, quota, and the
/// OAuth token endpoint. Error answers follow Dropbox's shape: 409 with <c>error_summary</c> and a tagged
/// <c>error</c>, 401 for tokens, 429 with <c>Retry-After</c>.
/// </summary>
internal sealed class FakeDropbox : HttpMessageHandler
{
    public const string AppKey = "fake-app-key";

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp" };

    private sealed class Node(string path, bool isFolder, byte[] content, string id, DateTimeOffset modified)
    {
        public string Path { get; set; } = path;
        public bool IsFolder { get; } = isFolder;
        public byte[] Content { get; set; } = content;
        public string Id { get; } = id;
        public int Rev { get; set; } = 1;
        public DateTimeOffset Modified { get; set; } = modified;
        public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    }

    private sealed record Job(JsonNode Result, int PollsLeft)
    {
        public int PollsLeft { get; set; } = PollsLeft;
    }

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<JsonNode>> _listCursors = [];
    private readonly Dictionary<string, (string Folder, string Query, int Offset, int PageSize)> _searchCursors = [];
    private readonly Dictionary<string, MemoryStream> _sessions = [];
    private readonly Dictionary<string, Job> _jobs = [];
    private readonly Dictionary<string, string> _links = [];
    private readonly Dictionary<string, (string Challenge, string? RedirectUri)> _codes = [];
    private readonly HashSet<string> _refreshTokens = [];
    private readonly Lock _gate = new();
    private int _ids;
    private DateTimeOffset _clock = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    public HashSet<string> ValidTokens { get; } = ["test-token"];
    public HashSet<string> ExpiredTokens { get; } = [];

    /// <summary>Entries per <c>list_folder</c> page, whatever <c>limit</c> asks for.</summary>
    public int ListPageSize { get; set; } = 1000;

    /// <summary>Matches per <c>search_v2</c> page at most.</summary>
    public int SearchPageSize { get; set; } = 1000;

    /// <summary>Off: <c>permanently_delete</c> is refused as for a non-Business app, and nothing is deleted.</summary>
    public bool PermanentDeleteAllowed { get; set; } = true;

    /// <summary>A copy or move of more entries is refused with <c>too_many_files</c>, as Dropbox does past 10 000.</summary>
    public int? TooManyFilesLimit { get; set; }

    /// <summary>How many <c>check_v2</c> polls answer <c>in_progress</c> before a batch job completes.</summary>
    public int JobPolls { get; set; } = 1;

    /// <summary>Whether <c>shared_link_already_exists</c> carries the existing link's metadata.</summary>
    public bool ExistingLinkInError { get; set; } = true;

    /// <summary>The next this many API calls are answered 429 with <see cref="RetryAfterSeconds"/>.</summary>
    public int RateLimitedRequests { get; set; }

    public int RetryAfterSeconds { get; set; }

    public long Quota { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Every call: route (<c>files/upload</c>, <c>oauth2/token</c>), the raw <c>Dropbox-API-Arg</c>, body length, token.</summary>
    public List<(string Route, string? Arg, long BodyLength, string? Token)> Requests { get; } = [];

    public List<string> DeletedPaths { get; } = [];
    public List<string> PermanentlyDeletedPaths { get; } = [];

    /// <summary>The settings of the last <c>create_shared_link_with_settings</c>.</summary>
    public JsonNode? LastLinkSettings { get; private set; }

    /// <summary>The form of the last <c>oauth2/token</c> call.</summary>
    public Dictionary<string, string>? LastTokenForm { get; private set; }

    public DropboxClient CreateClient(DropboxClientOptions? options = null, string token = "test-token") =>
        new(_ => ValueTask.FromResult(token), new HttpClient(this), options);

    public IEnumerable<string> Routes => Requests.Select(r => r.Route);

    // ── Seeding and inspecting ──────────────────────────────────────────────

    public void AddFile(string path, byte[] content)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[path] = new Node(path, false, content, NextId(), Tick());
        }
    }

    public void AddFile(string path, string text) => AddFile(path, Encoding.UTF8.GetBytes(text));

    public void AddFolder(string path)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes.TryAdd(path, new Node(path, true, [], NextId(), Tick()));
        }
    }

    public byte[]? Read(string path)
    {
        lock (_gate)
            return _nodes.TryGetValue(path, out var node) && !node.IsFolder ? node.Content : null;
    }

    public bool FolderExists(string path)
    {
        lock (_gate)
            return _nodes.TryGetValue(path, out var node) && node.IsFolder;
    }

    /// <summary>The path as Dropbox would display it (its stored casing), or null.</summary>
    public string? DisplayPath(string path)
    {
        lock (_gate)
            return _nodes.TryGetValue(path, out var node) ? node.Path : null;
    }

    public bool HasLink(string path)
    {
        lock (_gate)
            return _links.ContainsValue(path.ToLowerInvariant());
    }

    /// <summary>Lets <c>oauth2/token</c> exchange <paramref name="code"/> for the verifier of <paramref name="challenge"/>.</summary>
    public void ExpectCode(string code, string challenge, string? redirectUri)
    {
        lock (_gate)
            _codes[code] = (challenge, redirectUri);
    }

    public void AddRefreshToken(string refreshToken)
    {
        lock (_gate)
            _refreshTokens.Add(refreshToken);
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var uri = request.RequestUri!;
        var route = uri.AbsolutePath.TrimStart('/');
        route = route.StartsWith("2/") ? route[2..] : route;
        var arg = request.Headers.TryGetValues("Dropbox-API-Arg", out var values) ? values.Single() : null;
        var token = request.Headers.Authorization?.Parameter;

        lock (_gate)
        {
            Requests.Add((route, arg, body.Length, token));

            if (route == "oauth2/token")
                return Token(Encoding.UTF8.GetString(body));

            if (token is null || ExpiredTokens.Contains(token))
                return Error(HttpStatusCode.Unauthorized, token is null ? "missing_auth" : "expired_access_token");

            if (!ValidTokens.Contains(token))
                return Error(HttpStatusCode.Unauthorized, "invalid_access_token");

            if (RateLimitedRequests > 0)
            {
                RateLimitedRequests--;
                var limited = Json(HttpStatusCode.TooManyRequests, new JsonObject
                {
                    ["error_summary"] = "too_many_requests/..",
                    ["error"] = new JsonObject
                    {
                        ["reason"] = new JsonObject { [".tag"] = "too_many_requests" },
                        ["retry_after"] = RetryAfterSeconds
                    }
                });
                limited.Headers.TryAddWithoutValidation("Retry-After", RetryAfterSeconds.ToString());
                return limited;
            }

            var isContent = uri.Host == "content.dropboxapi.com";
            JsonNode? input;

            if (isContent)
            {
                if (arg is null)
                    return Text(HttpStatusCode.BadRequest, "Missing Dropbox-API-Arg header");

                if (arg.Any(c => c > 0x7E || c < 0x20))
                    return Text(HttpStatusCode.BadRequest, "Dropbox-API-Arg must be ASCII; escape other characters as \\uXXXX");

                input = JsonNode.Parse(arg);
            }
            else
            {
                if (route == "users/get_space_usage" || route == "auth/token/revoke")
                {
                    if (body.Length > 0 && Encoding.UTF8.GetString(body) != "null")
                        return Text(HttpStatusCode.BadRequest, "This route takes no argument");

                    input = null;
                }
                else
                {
                    if (request.Content?.Headers.ContentType?.MediaType != "application/json")
                        return Text(HttpStatusCode.BadRequest, "Bad HTTP \"Content-Type\" header");

                    input = JsonNode.Parse(body);
                }
            }

            try
            {
                return Dispatch(route, input, body, token);
            }
            catch (FakeError e)
            {
                return e.Response;
            }
        }
    }

    private HttpResponseMessage Dispatch(string route, JsonNode? input, byte[] body, string token) => route switch
    {
        "files/list_folder" => ListFolder(Path(input, "path")),
        "files/list_folder/continue" => ListContinue(Str(input, "cursor")),
        "files/get_metadata" => Ok(Metadata(Lookup(Path(input, "path"), "path"))),
        "files/create_folder_v2" => CreateFolder(Path(input, "path")),
        "files/delete_v2" => Delete(Path(input, "path")),
        "files/permanently_delete" => PermanentlyDelete(Path(input, "path")),
        "files/copy_v2" => Ok(new JsonObject { ["metadata"] = Relocate(Path(input, "from_path"), Path(input, "to_path"), false) }),
        "files/move_v2" => Ok(new JsonObject { ["metadata"] = Relocate(Path(input, "from_path"), Path(input, "to_path"), true) }),
        "files/copy_batch_v2" => StartJob(input!, false),
        "files/move_batch_v2" => StartJob(input!, true),
        "files/copy_batch/check_v2" or "files/move_batch/check_v2" => CheckJob(Str(input, "async_job_id")),
        "files/upload" => Ok(Commit(input!, body)),
        "files/upload_session/start" => StartSession(body),
        "files/upload_session/append_v2" => AppendSession(input!["cursor"]!, body),
        "files/upload_session/finish" => FinishSession(input!, body),
        "files/download" => Download(Path(input, "path")),
        "files/get_thumbnail_v2" => Thumbnail(input!),
        "files/search_v2" => Search(input!),
        "files/search/continue_v2" => SearchPage(Str(input, "cursor")),
        "users/get_space_usage" => SpaceUsage(),
        "sharing/create_shared_link_with_settings" => CreateLink(Path(input, "path"), input!["settings"]),
        "sharing/list_shared_links" => ListLinks(Path(input, "path")),
        "sharing/revoke_shared_link" => RevokeLink(Str(input, "url")),
        "auth/token/revoke" => Revoke(token),
        _ => Text(HttpStatusCode.BadRequest, $"Unknown API function: \"{route}\"")
    };

    // ── Files ───────────────────────────────────────────────────────────────

    private HttpResponseMessage ListFolder(string path)
    {
        if (path.Length > 0)
        {
            var folder = Lookup(path, "path");

            if (!folder.IsFolder)
                throw Conflict("path/not_folder/..", Tagged("path", Tagged("not_folder")));
        }

        var entries = Children(path).OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .Select(n => (JsonNode)Metadata(n)).ToList();
        return ListPage(entries);
    }

    private HttpResponseMessage ListContinue(string cursor)
    {
        if (!_listCursors.Remove(cursor, out var rest))
            throw Conflict("reset/..", Tagged("reset"));

        return ListPage(rest);
    }

    private HttpResponseMessage ListPage(List<JsonNode> entries)
    {
        var page = entries.Take(ListPageSize).ToList();
        var rest = entries.Skip(ListPageSize).ToList();
        var cursor = $"list-{NextId()}";

        if (rest.Count > 0)
            _listCursors[cursor] = rest;

        return Ok(new JsonObject
        {
            ["entries"] = new JsonArray(page.ToArray()),
            ["cursor"] = cursor,
            ["has_more"] = rest.Count > 0
        });
    }

    private HttpResponseMessage CreateFolder(string path)
    {
        if (_nodes.TryGetValue(path, out var existing))
            throw Conflict($"path/conflict/{(existing.IsFolder ? "folder" : "file")}/..",
                Tagged("path", Tagged("conflict", Tagged(existing.IsFolder ? "folder" : "file"))));

        EnsureParents(path);
        var node = _nodes[path] = new Node(path, true, [], NextId(), Tick());
        return Ok(new JsonObject { ["metadata"] = Metadata(node) });
    }

    private HttpResponseMessage Delete(string path)
    {
        var node = Lookup(path, "path_lookup");
        var metadata = Metadata(node);
        RemoveTree(node.Path);
        DeletedPaths.Add(node.Path);
        return Ok(new JsonObject { ["metadata"] = metadata });
    }

    private HttpResponseMessage PermanentlyDelete(string path)
    {
        if (!PermanentDeleteAllowed)
            return Text(HttpStatusCode.BadRequest,
                "Error in call to API function \"files/permanently_delete\": This endpoint is only available for Dropbox Business apps.");

        var node = Lookup(path, "path_lookup");
        RemoveTree(node.Path);
        PermanentlyDeletedPaths.Add(node.Path);
        return Ok(null);
    }

    private JsonObject Relocate(string from, string to, bool move)
    {
        var source = Lookup(from, "from_lookup");

        if (_nodes.TryGetValue(to, out var existing))
            throw Conflict($"to/conflict/{(existing.IsFolder ? "folder" : "file")}/..",
                Tagged("to", Tagged("conflict", Tagged(existing.IsFolder ? "folder" : "file"))));

        if (to.StartsWith(source.Path + "/", StringComparison.OrdinalIgnoreCase))
            throw Conflict("cant_move_folder_into_itself/..", Tagged("cant_move_folder_into_itself"));

        var tree = Tree(source.Path).ToList();

        if (tree.Count > TooManyFilesLimit)
            throw Conflict("too_many_files/..", Tagged("too_many_files"));

        EnsureParents(to);

        foreach (var node in tree)
        {
            var target = to + node.Path[source.Path.Length..];

            if (move)
            {
                _nodes.Remove(node.Path);
                node.Path = target;
                _nodes[target] = node;
            }
            else
            {
                _nodes[target] = new Node(target, node.IsFolder, node.Content, NextId(), node.Modified);
            }
        }

        return Metadata(_nodes[to]);
    }

    private HttpResponseMessage StartJob(JsonNode input, bool move)
    {
        var entries = input["entries"]!.AsArray();
        var results = new JsonArray();

        foreach (var entry in entries)
        {
            try
            {
                var saved = TooManyFilesLimit;
                TooManyFilesLimit = null;

                try
                {
                    results.Add(new JsonObject
                    {
                        [".tag"] = "success",
                        ["success"] = Relocate(Path(entry, "from_path"), Path(entry, "to_path"), move)
                    });
                }
                finally
                {
                    TooManyFilesLimit = saved;
                }
            }
            catch (FakeError e)
            {
                results.Add(new JsonObject
                {
                    [".tag"] = "failure",
                    ["failure"] = new JsonObject { [".tag"] = "relocation_error", ["relocation_error"] = e.Error.DeepClone() }
                });
            }
        }

        var id = $"job-{NextId()}";
        _jobs[id] = new Job(new JsonObject { [".tag"] = "complete", ["entries"] = results }, JobPolls);
        return Ok(new JsonObject { [".tag"] = "async_job_id", ["async_job_id"] = id });
    }

    private HttpResponseMessage CheckJob(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
            throw Conflict("invalid_async_job_id/..", Tagged("invalid_async_job_id"));

        if (job.PollsLeft-- > 0)
            return Ok(new JsonObject { [".tag"] = "in_progress" });

        return Ok(job.Result.DeepClone());
    }

    private JsonObject Commit(JsonNode commit, byte[] content)
    {
        var path = Path(commit, "path");
        var mode = commit["mode"] is JsonObject tagged ? (string)tagged[".tag"]! : (string?)commit["mode"] ?? "add";

        if (commit["autorename"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("FileVault resolves names itself; autorename must stay off.");

        if (_nodes.TryGetValue(path, out var existing))
        {
            if (existing.IsFolder || mode != "overwrite")
                throw Conflict($"path/conflict/{(existing.IsFolder ? "folder" : "file")}/..",
                    Tagged("path", Tagged("reason", Tagged("conflict", Tagged(existing.IsFolder ? "folder" : "file")))));

            existing.Content = content;
            existing.Rev++;
            existing.Modified = Tick();
            return Metadata(existing, withTag: false);
        }

        EnsureParents(path);
        var node = _nodes[path] = new Node(path, false, content, NextId(), Tick());
        return Metadata(node, withTag: false);
    }

    private HttpResponseMessage StartSession(byte[] body)
    {
        var id = $"session-{NextId()}";
        _sessions[id] = new MemoryStream();
        _sessions[id].Write(body);
        return Ok(new JsonObject { ["session_id"] = id });
    }

    private HttpResponseMessage AppendSession(JsonNode cursor, byte[] body)
    {
        Session(cursor).Write(body);
        return Ok(null);
    }

    private HttpResponseMessage FinishSession(JsonNode input, byte[] body)
    {
        var session = Session(input["cursor"]!);
        session.Write(body);
        _sessions.Remove((string)input["cursor"]!["session_id"]!);
        return Ok(Commit(input["commit"]!, session.ToArray()));
    }

    private MemoryStream Session(JsonNode cursor)
    {
        if (!_sessions.TryGetValue((string)cursor["session_id"]!, out var session))
            throw Conflict("not_found/..", Tagged("not_found"));

        var offset = cursor["offset"]!.GetValue<long>();

        if (offset != session.Length)
            throw Conflict("incorrect_offset/..", new JsonObject
            {
                [".tag"] = "incorrect_offset",
                ["correct_offset"] = session.Length
            });

        return session;
    }

    private HttpResponseMessage Download(string path)
    {
        var node = Lookup(path, "path");

        if (node.IsFolder)
            throw Conflict("path/not_file/..", Tagged("path", Tagged("not_file")));

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(node.Content) };
        response.Headers.TryAddWithoutValidation("Dropbox-API-Result",
            DropboxApi.EscapeNonAscii(Metadata(node, withTag: false).ToJsonString()));
        return response;
    }

    private HttpResponseMessage Thumbnail(JsonNode input)
    {
        var resource = input["resource"]!;

        if ((string?)resource[".tag"] != "path")
            return Text(HttpStatusCode.BadRequest, "Only path resources are faked");

        var node = Lookup(Path(resource, "path"), "path");

        if (!ImageExtensions.Contains(System.IO.Path.GetExtension(node.Name)))
            throw Conflict("unsupported_extension/..", Tagged("unsupported_extension"));

        var picture = Encoding.UTF8.GetBytes($"JPEG {input["size"]} {input["mode"]} {input["format"]} {node.Name}");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) };
        response.Headers.TryAddWithoutValidation("Dropbox-API-Result",
            DropboxApi.EscapeNonAscii(Metadata(node, withTag: false).ToJsonString()));
        return response;
    }

    private HttpResponseMessage Search(JsonNode input)
    {
        var options = input["options"];
        var folder = options?["path"] is { } path ? (string)path! : "";

        if (folder.Length > 0)
            Lookup(folder, "path");

        if (options?["filename_only"]?.GetValue<bool>() != true)
            return Text(HttpStatusCode.BadRequest, "The fake searches file names only");

        var pageSize = Math.Min(SearchPageSize, options?["max_results"]?.GetValue<int>() ?? 100);
        return SearchResult(folder, (string)input["query"]!, 0, pageSize);
    }

    private HttpResponseMessage SearchPage(string cursor)
    {
        if (!_searchCursors.Remove(cursor, out var state))
            throw Conflict("invalid_cursor/..", Tagged("invalid_cursor"));

        return SearchResult(state.Folder, state.Query, state.Offset, state.PageSize);
    }

    private HttpResponseMessage SearchResult(string folder, string query, int offset, int pageSize)
    {
        var found = _nodes.Values
            .Where(n => folder.Length == 0 || n.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            .Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var page = found.Skip(offset).Take(pageSize).ToList();
        var hasMore = offset + page.Count < found.Count;
        var result = new JsonObject
        {
            ["matches"] = new JsonArray(page.Select(n => (JsonNode)new JsonObject
            {
                ["match_type"] = new JsonObject { [".tag"] = "filename" },
                ["metadata"] = new JsonObject { [".tag"] = "metadata", ["metadata"] = Metadata(n) }
            }).ToArray()),
            ["has_more"] = hasMore
        };

        if (hasMore)
        {
            var cursor = $"search-{NextId()}";
            _searchCursors[cursor] = (folder, query, offset + page.Count, pageSize);
            result["cursor"] = cursor;
        }

        return Ok(result);
    }

    private HttpResponseMessage SpaceUsage() => Ok(new JsonObject
    {
        ["used"] = _nodes.Values.Where(n => !n.IsFolder).Sum(n => (long)n.Content.Length),
        ["allocation"] = new JsonObject { [".tag"] = "individual", ["allocated"] = Quota }
    });

    // ── Shared links ────────────────────────────────────────────────────────

    private HttpResponseMessage CreateLink(string path, JsonNode? settings)
    {
        var node = Lookup(path, "path");
        LastLinkSettings = settings?.DeepClone();

        var existing = _links.FirstOrDefault(l => l.Value == node.Path.ToLowerInvariant()).Key;

        if (existing is not null)
            throw Conflict("shared_link_already_exists/..", ExistingLinkInError
                ? new JsonObject
                {
                    [".tag"] = "shared_link_already_exists",
                    ["shared_link_already_exists"] = new JsonObject { [".tag"] = "metadata", ["metadata"] = Link(existing, node) }
                }
                : Tagged("shared_link_already_exists"));

        var url = $"https://www.dropbox.com/scl/fi/{node.Id}/{Uri.EscapeDataString(node.Name)}?rlkey=fake{_ids}&dl=0";
        _links[url] = node.Path.ToLowerInvariant();
        return Ok(Link(url, node));
    }

    private HttpResponseMessage ListLinks(string path)
    {
        var links = _links.Where(l => l.Value == path.ToLowerInvariant())
            .Select(l => (JsonNode)Link(l.Key, _nodes[l.Value]))
            .ToArray();

        return Ok(new JsonObject { ["links"] = new JsonArray(links), ["has_more"] = false });
    }

    private HttpResponseMessage RevokeLink(string url)
    {
        if (!_links.Remove(url))
            throw Conflict("shared_link_not_found/..", Tagged("shared_link_not_found"));

        return Ok(null);
    }

    private static JsonObject Link(string url, Node node) => new()
    {
        [".tag"] = node.IsFolder ? "folder" : "file",
        ["url"] = url,
        ["name"] = node.Name,
        ["path_lower"] = node.Path.ToLowerInvariant(),
        ["link_permissions"] = new JsonObject { ["can_revoke"] = true }
    };

    // ── OAuth ───────────────────────────────────────────────────────────────

    private HttpResponseMessage Token(string body)
    {
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')),
                p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));
        LastTokenForm = form;

        if (form.GetValueOrDefault("client_id") != AppKey)
            return OAuthError("invalid_client", "Invalid client_id");

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
            {
                if (!_codes.Remove(form.GetValueOrDefault("code") ?? "", out var expected))
                    return OAuthError("invalid_grant", "code doesn't exist or has expired");

                var verifier = form.GetValueOrDefault("code_verifier") ?? "";
                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');

                if (challenge != expected.Challenge)
                    return OAuthError("invalid_grant", "invalid code verifier");

                if (form.GetValueOrDefault("redirect_uri") != expected.RedirectUri)
                    return OAuthError("invalid_grant", "redirect_uri mismatch");

                var access = $"sl.access-{NextId()}";
                var refresh = $"refresh-{NextId()}";
                ValidTokens.Add(access);
                _refreshTokens.Add(refresh);

                return Ok(new JsonObject
                {
                    ["access_token"] = access,
                    ["token_type"] = "bearer",
                    ["expires_in"] = 14400,
                    ["refresh_token"] = refresh,
                    ["scope"] = "files.content.read files.content.write",
                    ["uid"] = "12345",
                    ["account_id"] = "dbid:fake"
                });
            }
            case "refresh_token":
            {
                if (form.ContainsKey("code_verifier"))
                    return OAuthError("invalid_request", "unknown field \"code_verifier\"");

                if (!_refreshTokens.Contains(form.GetValueOrDefault("refresh_token") ?? ""))
                    return OAuthError("invalid_grant", "refresh token is invalid or revoked");

                var access = $"sl.refreshed-{NextId()}";
                ValidTokens.Add(access);

                return Ok(new JsonObject
                {
                    ["access_token"] = access,
                    ["token_type"] = "bearer",
                    ["expires_in"] = 14400
                });
            }
            default:
                return OAuthError("unsupported_grant_type", null);
        }
    }

    private HttpResponseMessage Revoke(string token)
    {
        ValidTokens.Remove(token);
        return Ok(null);
    }

    private static HttpResponseMessage OAuthError(string error, string? description) =>
        Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = error, ["error_description"] = description });

    // ── Tree helpers ────────────────────────────────────────────────────────

    private Node Lookup(string path, string tag)
    {
        if (path.Length == 0)
            return new Node("", true, [], "id:root", _clock);

        return _nodes.TryGetValue(path, out var node)
            ? node
            : throw Conflict($"{tag}/not_found/..", Tagged(tag, Tagged("not_found")));
    }

    private IEnumerable<Node> Children(string folder) =>
        _nodes.Values.Where(n => string.Equals(Parent(n.Path), folder, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<Node> Tree(string path) =>
        _nodes.Values.Where(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)
                                 || n.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

    private void RemoveTree(string path)
    {
        foreach (var node in Tree(path).ToList())
            _nodes.Remove(node.Path);
    }

    private void EnsureParents(string path)
    {
        for (var parent = Parent(path); parent.Length > 0; parent = Parent(parent))
        {
            if (_nodes.TryGetValue(parent, out var node))
            {
                if (!node.IsFolder)
                    throw Conflict("path/conflict/file_ancestor/..", Tagged("path", Tagged("conflict", Tagged("file_ancestor"))));

                continue;
            }

            _nodes[parent] = new Node(parent, true, [], NextId(), Tick());
        }
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "" : path[..slash];
    }

    private JsonObject Metadata(Node node, bool withTag = true)
    {
        var metadata = new JsonObject();

        if (withTag)
            metadata[".tag"] = node.IsFolder ? "folder" : "file";

        metadata["name"] = node.Name;
        metadata["path_lower"] = node.Path.ToLowerInvariant();
        metadata["path_display"] = node.Path;
        metadata["id"] = node.Id;

        if (!node.IsFolder)
        {
            metadata["client_modified"] = node.Modified.ToString("yyyy-MM-ddTHH:mm:ssZ");
            metadata["server_modified"] = node.Modified.ToString("yyyy-MM-ddTHH:mm:ssZ");
            metadata["rev"] = $"{node.Rev:x9}";
            metadata["size"] = node.Content.Length;
            metadata["content_hash"] = Convert.ToHexStringLower(SHA256.HashData(node.Content));
            metadata["is_downloadable"] = true;
        }

        return metadata;
    }

    private string NextId() => $"id:{++_ids}";

    private DateTimeOffset Tick() => _clock = _clock.AddMinutes(1);

    private static string Path(JsonNode? input, string name)
    {
        var path = (string?)input?[name] ?? throw new FakeError(Text(HttpStatusCode.BadRequest, $"missing {name}"), new JsonObject());

        if (path.Length > 0 && (!path.StartsWith('/') || path.EndsWith('/')))
            throw new FakeError(Text(HttpStatusCode.BadRequest, $"malformed path '{path}'"), new JsonObject());

        return path;
    }

    private static string Str(JsonNode? input, string name) =>
        (string?)input?[name] ?? throw new FakeError(Text(HttpStatusCode.BadRequest, $"missing {name}"), new JsonObject());

    private static JsonObject Tagged(string tag, JsonObject? inner = null)
    {
        var node = new JsonObject { [".tag"] = tag };

        if (inner is not null)
            node[tag] = inner;

        return node;
    }

    private static FakeError Conflict(string summary, JsonObject error) =>
        new(Json(HttpStatusCode.Conflict, new JsonObject { ["error_summary"] = summary, ["error"] = error.DeepClone() }), error);

    private static HttpResponseMessage Error(HttpStatusCode status, string tag) =>
        Json(status, new JsonObject { ["error_summary"] = $"{tag}/..", ["error"] = Tagged(tag) });

    private static HttpResponseMessage Ok(JsonNode? body) => Json(HttpStatusCode.OK, body);

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode? body) => new(status)
    {
        Content = new StringContent(body?.ToJsonString() ?? "null", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Text(HttpStatusCode status, string text) => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain")
    };

    private sealed class FakeError(HttpResponseMessage response, JsonObject error) : Exception
    {
        public HttpResponseMessage Response { get; } = response;
        public JsonObject Error { get; } = error;
    }
}
