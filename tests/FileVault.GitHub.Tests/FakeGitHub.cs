using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FileVault.GitHub.Tests;

/// <summary>
/// An in-memory GitHub: blobs, trees, commits and branches of one repository behind the REST endpoints
/// <see cref="GitHubRepository"/> calls, plus Git LFS. Objects are content-addressed like in git, so a tree that did
/// not change keeps its sha.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public const string Owner = "anna";
    public const string Repo = "art-life";

    private sealed record TreeItem(string Name, string Mode, string Type, string Sha);

    private sealed record CommitObject(
        string Sha, string Tree, List<string> Parents, string Message,
        string AuthorName, string AuthorEmail, string? Login, DateTimeOffset Date);

    private readonly Dictionary<string, byte[]> _blobs = [];
    private readonly Dictionary<string, List<TreeItem>> _trees = [];
    private readonly Dictionary<string, CommitObject> _commits = [];
    private readonly Dictionary<string, string> _branches = [];
    private readonly Dictionary<string, byte[]> _lfs = [];
    private readonly Lock _gate = new();
    private DateTimeOffset _clock = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    public FakeGitHub()
    {
        var empty = PutTree([]);
        _branches["main"] = PutCommit(empty, [], "Initial commit", "Anna", "anna@example.com", "anna");
    }

    /// <summary>Every request as "METHOD url", with the Authorization scheme it carried.</summary>
    public List<(string Request, string? Authorization)> Requests { get; } = [];

    /// <summary>Runs once just before the next branch update is checked: a push that lands in between.</summary>
    public Action? BeforeRefUpdate { get; set; }

    public int CommitCount => _commits.Count;

    /// <summary>A recursive tree with more entries than this is cut short and marked truncated, as GitHub does past 100 000.</summary>
    public int? RecursiveTreeLimit { get; set; }

    /// <summary>The Git Trees API calls made so far.</summary>
    public int TreeRequests => Requests.Count(r => r.Request.StartsWith("GET ") && r.Request.Contains("/git/trees/"));

    public GitHubRepository CreateRepository(string branch = "main", string? token = "test-token") =>
        new(new GitHubRepositoryOptions
        {
            Owner = Owner,
            Name = Repo,
            Branch = branch,
            Token = token,
            HeadCacheDuration = TimeSpan.Zero
        }, new HttpClient(this));

    // ── Seeding and inspecting ──────────────────────────────────────────────

    /// <summary>A commit on <paramref name="branch"/> putting files (null content deletes the path).</summary>
    public string Commit(Dictionary<string, byte[]?> files, string message = "Seed", string branch = "main",
        string author = "Anna", string email = "anna@example.com", string? login = "anna")
    {
        lock (_gate)
        {
            var head = _commits[_branches[branch]];
            var tree = Apply(head.Tree, files.Select(f =>
                (f.Key, f.Value is null ? null : new TreeItem("", "100644", "blob", PutBlob(f.Value)))).ToList());

            return _branches[branch] = PutCommit(tree, [head.Sha], message, author, email, login);
        }
    }

    public string Commit(string path, string text, string message = "Seed", string branch = "main",
        string author = "Anna", string email = "anna@example.com", string? login = "anna") =>
        Commit(new Dictionary<string, byte[]?> { [path] = Encoding.UTF8.GetBytes(text) }, message, branch, author,
            email, login);

    public void CreateBranch(string name, string from = "main")
    {
        lock (_gate)
            _branches[name] = _branches[from];
    }

    public string Head(string branch = "main") => _branches[branch];

    public byte[]? Read(string path, string branch = "main")
    {
        lock (_gate)
            return Find(_commits[_branches[branch]].Tree, path) is { Type: "blob" } item ? _blobs[item.Sha] : null;
    }

    public bool FolderExists(string path, string branch = "main")
    {
        lock (_gate)
            return Find(_commits[_branches[branch]].Tree, path) is { Type: "tree" };
    }

    public string? Message(string commitSha) => _commits.GetValueOrDefault(commitSha)?.Message;

    /// <summary>Stores an LFS object and returns the pointer file git would keep for it.</summary>
    public byte[] AddLfsObject(byte[] content)
    {
        var oid = Convert.ToHexStringLower(SHA256.HashData(content));
        _lfs[oid] = content;

        return Encoding.UTF8.GetBytes(
            $"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize {content.Length}\n");
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

        lock (_gate)
        {
            Requests.Add(($"{request.Method} {request.RequestUri}", request.Headers.Authorization?.Scheme));

            try
            {
                return Route(request, body);
            }
            catch (KeyNotFoundException)
            {
                return Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = "Not Found" });
            }
        }
    }

    private HttpResponseMessage Route(HttpRequestMessage request, string? body)
    {
        var uri = request.RequestUri!;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var method = request.Method.Method;

        if (uri.Host == "lfs.example")
            return Raw(_lfs[path["/objects/".Length..]]);

        if (uri.Host == "github.com" && path == $"/{Owner}/{Repo}.git/info/lfs/objects/batch")
            return LfsBatch(JsonNode.Parse(body!)!);

        var prefix = $"/repos/{Owner}/{Repo}";

        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = "Not Found" });

        var rest = path[prefix.Length..];

        return (method, rest) switch
        {
            ("GET", "") => Json(new JsonObject { ["default_branch"] = "main", ["private"] = true }),
            ("GET", _) when rest.StartsWith("/git/ref/heads/") => RefJson(rest["/git/ref/heads/".Length..]),
            ("PATCH", _) when rest.StartsWith("/git/refs/heads/") => UpdateRef(rest["/git/refs/heads/".Length..], JsonNode.Parse(body!)!),
            ("GET", _) when rest.StartsWith("/git/commits/") => Json(GitCommitJson(_commits[rest["/git/commits/".Length..]])),
            ("POST", "/git/commits") => CreateCommit(JsonNode.Parse(body!)!),
            ("GET", _) when rest.StartsWith("/git/trees/") => Json(TreeJson(rest["/git/trees/".Length..], query["recursive"] == "1")),
            ("POST", "/git/trees") => CreateTree(JsonNode.Parse(body!)!),
            ("GET", _) when rest.StartsWith("/git/blobs/") => Raw(_blobs[rest["/git/blobs/".Length..]]),
            ("POST", "/git/blobs") => Json(HttpStatusCode.Created, new JsonObject
            {
                ["sha"] = PutBlob(Convert.FromBase64String(JsonNode.Parse(body!)!["content"]!.GetValue<string>()))
            }),
            ("GET", "/commits") => ListCommits(query["sha"]!, query["path"], int.Parse(query["per_page"] ?? "30"),
                int.Parse(query["page"] ?? "1")),
            ("GET", _) when rest.StartsWith("/commits/") => Json(RestCommitJson(_commits[Resolve(rest["/commits/".Length..])], true)),
            ("GET", "/branches") => Json(new JsonArray(_branches.Select(b => (JsonNode)new JsonObject
            {
                ["name"] = b.Key,
                ["commit"] = new JsonObject { ["sha"] = b.Value }
            }).ToArray())),
            ("GET", _) when rest.StartsWith("/compare/") => Compare(rest["/compare/".Length..]),
            _ => Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = "Not Found" })
        };
    }

    private HttpResponseMessage RefJson(string branch) => Json(new JsonObject
    {
        ["ref"] = $"refs/heads/{branch}",
        ["object"] = new JsonObject { ["sha"] = _branches[branch] }
    });

    private HttpResponseMessage UpdateRef(string branch, JsonNode body)
    {
        // Lock is reentrant, so the hook can seed through Commit
        if (BeforeRefUpdate is { } hook)
        {
            BeforeRefUpdate = null;
            hook();
        }

        var sha = body["sha"]!.GetValue<string>();
        var force = body["force"]?.GetValue<bool>() ?? false;

        if (!force && !Ancestors(sha).Contains(_branches[branch]))
            return Json(HttpStatusCode.UnprocessableEntity, new JsonObject { ["message"] = "Update is not a fast forward" });

        _branches[branch] = sha;
        return RefJson(branch);
    }

    private HttpResponseMessage CreateCommit(JsonNode body)
    {
        var sha = PutCommit(body["tree"]!.GetValue<string>(),
            body["parents"]!.AsArray().Select(p => p!.GetValue<string>()).ToList(),
            body["message"]!.GetValue<string>(), "Anna", "anna@example.com", "anna");

        return Json(HttpStatusCode.Created, GitCommitJson(_commits[sha]));
    }

    private HttpResponseMessage CreateTree(JsonNode body)
    {
        var changes = body["tree"]!.AsArray().Select(e =>
        {
            var sha = e!["sha"]?.GetValue<string>();
            var item = sha is null ? null : new TreeItem("", e["mode"]!.GetValue<string>(), e["type"]!.GetValue<string>(), sha);
            return (e["path"]!.GetValue<string>(), item);
        }).ToList();

        var tree = Apply(body["base_tree"]!.GetValue<string>(), changes);
        return Json(HttpStatusCode.Created, new JsonObject { ["sha"] = tree });
    }

    private HttpResponseMessage ListCommits(string from, string? path, int perPage, int page)
    {
        var commits = FirstParentChain(Resolve(from))
            .Where(c => path is null || TouchesPath(c, path))
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(c => (JsonNode)RestCommitJson(c, false))
            .ToArray();

        return Json(new JsonArray(commits));
    }

    private HttpResponseMessage Compare(string range)
    {
        var parts = range.Split("...");
        var baseAncestors = Ancestors(Resolve(parts[0]));
        var headAncestors = Ancestors(Resolve(parts[1]));
        var ahead = headAncestors.Count(c => !baseAncestors.Contains(c));
        var behind = baseAncestors.Count(c => !headAncestors.Contains(c));

        return Json(new JsonObject
        {
            ["status"] = (ahead, behind) switch
            {
                (0, 0) => "identical",
                (_, 0) => "ahead",
                (0, _) => "behind",
                _ => "diverged"
            },
            ["ahead_by"] = ahead,
            ["behind_by"] = behind
        });
    }

    private HttpResponseMessage LfsBatch(JsonNode body)
    {
        var objects = body["objects"]!.AsArray().Select(o =>
        {
            var oid = o!["oid"]!.GetValue<string>();

            return (JsonNode)(_lfs.ContainsKey(oid)
                ? new JsonObject
                {
                    ["oid"] = oid,
                    ["size"] = o["size"]!.GetValue<long>(),
                    ["actions"] = new JsonObject
                    {
                        ["download"] = new JsonObject
                        {
                            ["href"] = $"https://lfs.example/objects/{oid}",
                            ["header"] = new JsonObject { ["X-Signed"] = "yes" }
                        }
                    }
                }
                : new JsonObject
                {
                    ["oid"] = oid,
                    ["size"] = o["size"]!.GetValue<long>(),
                    ["error"] = new JsonObject { ["code"] = 404, ["message"] = "Object does not exist" }
                });
        }).ToArray();

        return Json(new JsonObject { ["objects"] = new JsonArray(objects) });
    }

    // ── Git objects ─────────────────────────────────────────────────────────

    private string PutBlob(byte[] content)
    {
        var sha = GitHubRepository.BlobSha(content);
        _blobs[sha] = content;
        return sha;
    }

    private string PutTree(IEnumerable<TreeItem> items)
    {
        var sorted = items.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        var sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(
            "tree " + string.Join("\n", sorted.Select(i => $"{i.Mode} {i.Type} {i.Sha} {i.Name}")))));
        _trees[sha] = sorted;
        return sha;
    }

    private string PutCommit(string tree, List<string> parents, string message, string author, string email, string? login)
    {
        _clock = _clock.AddMinutes(1);
        var sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"commit {tree} {string.Join(',', parents)} {message} {author} {_clock:O}")));
        _commits[sha] = new CommitObject(sha, tree, parents, message, author, email, login, _clock);
        return sha;
    }

    /// <summary>Applies path changes to a tree the way the Git Data API does: folders appear and empty ones vanish.</summary>
    private string Apply(string? treeSha, IReadOnlyList<(string Path, TreeItem? Item)> changes)
    {
        var items = (treeSha is null ? [] : _trees[treeSha]).ToDictionary(i => i.Name);

        foreach (var (path, item) in changes.Where(c => !c.Path.Contains('/')))
        {
            if (item is null)
                items.Remove(path);
            else
                items[path] = item with { Name = path };
        }

        foreach (var group in changes.Where(c => c.Path.Contains('/')).GroupBy(c => c.Path[..c.Path.IndexOf('/')]))
        {
            var sub = items.TryGetValue(group.Key, out var existing) && existing.Type == "tree" ? existing.Sha : null;
            var newSub = Apply(sub, group.Select(c => (c.Path[(c.Path.IndexOf('/') + 1)..], c.Item)).ToList());

            if (_trees[newSub].Count == 0)
                items.Remove(group.Key);
            else
                items[group.Key] = new TreeItem(group.Key, "040000", "tree", newSub);
        }

        return PutTree(items.Values);
    }

    private TreeItem? Find(string treeSha, string path)
    {
        TreeItem? item = null;
        var tree = treeSha;

        foreach (var segment in path.Split('/'))
        {
            if (tree is null)
                return null;

            item = _trees[tree].FirstOrDefault(i => i.Name == segment);

            if (item is null)
                return null;

            tree = item.Type == "tree" ? item.Sha : null;
        }

        return item;
    }

    private IEnumerable<(string Path, TreeItem Item)> Flatten(string treeSha, string prefix = "")
    {
        foreach (var item in _trees[treeSha])
        {
            var path = prefix + item.Name;
            yield return (path, item);

            if (item.Type == "tree")
                foreach (var nested in Flatten(item.Sha, path + "/"))
                    yield return nested;
        }
    }

    private string Resolve(string refOrSha) => _branches.TryGetValue(refOrSha, out var sha) ? sha : _commits[refOrSha].Sha;

    private IEnumerable<CommitObject> FirstParentChain(string sha)
    {
        for (var commit = _commits[sha]; ; commit = _commits[commit.Parents[0]])
        {
            yield return commit;

            if (commit.Parents.Count == 0)
                yield break;
        }
    }

    private HashSet<string> Ancestors(string sha)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>([sha]);

        while (stack.TryPop(out var current))
        {
            if (!seen.Add(current))
                continue;

            foreach (var parent in _commits[current].Parents)
                stack.Push(parent);
        }

        return seen;
    }

    private bool TouchesPath(CommitObject commit, string path)
    {
        var before = commit.Parents.Count == 0 ? null : Find(_commits[commit.Parents[0]].Tree, path)?.Sha;
        return Find(commit.Tree, path)?.Sha != before;
    }

    // ── JSON ────────────────────────────────────────────────────────────────

    private JsonObject TreeJson(string sha, bool recursive)
    {
        var entries = recursive
            ? Flatten(sha).Select(e => (e.Path, e.Item)).ToList()
            : _trees[sha].Select(i => (Path: i.Name, Item: i)).ToList();
        var truncated = recursive && entries.Count > RecursiveTreeLimit;

        if (truncated)
            entries = entries.Take(RecursiveTreeLimit!.Value).ToList();

        return new JsonObject
        {
            ["sha"] = sha,
            ["truncated"] = truncated,
            ["tree"] = new JsonArray(entries.Select(e =>
            {
                var node = new JsonObject
                {
                    ["path"] = e.Path,
                    ["mode"] = e.Item.Mode,
                    ["type"] = e.Item.Type,
                    ["sha"] = e.Item.Sha
                };

                if (e.Item.Type == "blob")
                    node["size"] = _blobs[e.Item.Sha].Length;

                return (JsonNode)node;
            }).ToArray())
        };
    }

    private static JsonObject Signature(CommitObject c) => new()
    {
        ["name"] = c.AuthorName,
        ["email"] = c.AuthorEmail,
        ["date"] = c.Date.ToString("O")
    };

    private static JsonObject GitCommitJson(CommitObject c) => new()
    {
        ["sha"] = c.Sha,
        ["tree"] = new JsonObject { ["sha"] = c.Tree },
        ["author"] = Signature(c),
        ["committer"] = Signature(c),
        ["message"] = c.Message,
        ["parents"] = new JsonArray(c.Parents.Select(p => (JsonNode)new JsonObject { ["sha"] = p }).ToArray())
    };

    private JsonObject RestCommitJson(CommitObject c, bool withFiles)
    {
        var json = new JsonObject
        {
            ["sha"] = c.Sha,
            ["commit"] = new JsonObject
            {
                ["author"] = Signature(c),
                ["committer"] = Signature(c),
                ["message"] = c.Message
            },
            ["author"] = c.Login is null ? null : new JsonObject { ["login"] = c.Login },
            ["parents"] = new JsonArray(c.Parents.Select(p => (JsonNode)new JsonObject { ["sha"] = p }).ToArray())
        };

        if (!withFiles)
            return json;

        var before = c.Parents.Count == 0
            ? new Dictionary<string, string>()
            : Flatten(_commits[c.Parents[0]].Tree).Where(e => e.Item.Type == "blob").ToDictionary(e => e.Path, e => e.Item.Sha);
        var after = Flatten(c.Tree).Where(e => e.Item.Type == "blob").ToDictionary(e => e.Path, e => e.Item.Sha);

        var files = after
            .Where(a => !before.TryGetValue(a.Key, out var old) || old != a.Value)
            .Select(a => (a.Key, Status: before.ContainsKey(a.Key) ? "modified" : "added"))
            .Concat(before.Keys.Where(b => !after.ContainsKey(b)).Select(b => (Key: b, Status: "removed")))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => (JsonNode)new JsonObject { ["filename"] = f.Key, ["status"] = f.Status });

        json["files"] = new JsonArray(files.ToArray());
        return json;
    }

    private static HttpResponseMessage Json(JsonNode node) => Json(HttpStatusCode.OK, node);

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode node) =>
        new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Raw(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
}
