using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FileVault.Core;

namespace FileVault.GoogleDrive.Tests;

/// <summary>Folder sizes against an in-memory Drive tree answering <c>files.list</c> — no credentials needed.</summary>
[TestFixture]
public class GoogleDriveFolderSizeTests
{
    private static async Task<List<FolderSizes>> MeasureAsync(IFolderSizeProvider provider) =>
        await provider.MeasureFoldersAsync().ToListAsync();

    private static DriveTree SampleTree()
    {
        var tree = new DriveTree();
        tree.Folder("A", "a", "ROOT");
        tree.File("x", "x.bin", "A", 3);
        tree.Folder("B", "b", "A");
        tree.File("y", "y.bin", "B", 5);
        tree.Folder("C", "c", "ROOT");
        tree.File("top", "top.txt", "ROOT", 100);
        tree.File("doc", "notes", "A", null, "application/vnd.google-apps.document");
        tree.File("gone", "trashed.bin", "A", 1000, trashed: true);
        return tree;
    }

    [Test]
    public async Task Root_AddsUpEveryDepth_LevelByLevel()
    {
        var tree = SampleTree();
        using var service = tree.CreateService();

        var sizes = await MeasureAsync(new GoogleDriveFileProvider(service, "ROOT"));

        Assert.That(sizes[^1].IsComplete, Is.True);
        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 8, ["c"] = 0 }),
            "top.txt lies directly in the folder; Docs files have no size; trashed files are not counted");
        Assert.That(tree.Queries, Is.EqualTo(new[]
        {
            "('ROOT' in parents) and trashed = false",
            "('A' in parents or 'C' in parents) and trashed = false",
            "('B' in parents) and trashed = false"
        }));
    }

    [Test]
    public async Task Subfolder_MeasuresItsOwnSubfolders()
    {
        var tree = SampleTree();
        using var service = tree.CreateService();

        var sizes = await MeasureAsync(new GoogleDriveFileProvider(service, "A"));

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["b"] = 5 }));
    }

    [Test]
    public async Task ManyFolders_AreAskedFortyAtATime_AndPaged()
    {
        var tree = new DriveTree { PageSize = 7 };
        var expected = new Dictionary<string, long>();

        for (var i = 0; i < 45; i++)
        {
            tree.Folder($"F{i}", $"f{i}", "ROOT");
            tree.File($"file{i}", "data.bin", $"F{i}", i);
            expected[$"f{i}"] = i;
        }

        using var service = tree.CreateService();

        var sizes = await MeasureAsync(new GoogleDriveFileProvider(service, "ROOT"));

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(expected));
        var levelQueries = tree.Queries.Where(q => !q.StartsWith("('ROOT'")).Distinct().ToList();
        Assert.That(levelQueries, Has.Count.EqualTo(2));
        Assert.That(Regex.Count(levelQueries[0], "in parents"), Is.EqualTo(40));
        Assert.That(Regex.Count(levelQueries[1], "in parents"), Is.EqualTo(5));
        Assert.That(tree.PageTokens.Count(t => t is not null), Is.GreaterThan(0), "pages were followed");
    }

    /// <summary>A Drive folder tree behind <c>files.list</c> with <c>'id' in parents</c> queries joined by <c>or</c>.</summary>
    private sealed class DriveTree
    {
        private const string FolderMime = "application/vnd.google-apps.folder";
        private readonly List<JsonObject> _files = [];
        private readonly HashSet<string> _trashed = [];

        public int PageSize { get; init; } = 1000;
        public List<string> Queries { get; } = [];
        public List<string?> PageTokens { get; } = [];

        public void Folder(string id, string name, string parent) => Add(id, name, parent, FolderMime, null);

        public void File(string id, string name, string parent, long? size, string mime = "application/octet-stream",
            bool trashed = false)
        {
            Add(id, name, parent, mime, size);
            if (trashed)
                _trashed.Add(id);
        }

        private void Add(string id, string name, string parent, string mime, long? size)
        {
            var file = new JsonObject { ["id"] = id, ["name"] = name, ["mimeType"] = mime, ["parents"] = new JsonArray(parent) };
            if (size is { } bytes)
                file["size"] = bytes.ToString();
            _files.Add(file);
        }

        public Google.Apis.Drive.v3.DriveService CreateService() => FakeDrive.CreateService(Respond);

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var q = query["q"]!;
            Queries.Add(q);
            PageTokens.Add(query["pageToken"]);

            Assert.That(q, Does.EndWith(" and trashed = false"));
            var parents = Regex.Matches(q, @"'([^']+)' in parents").Select(m => m.Groups[1].Value).ToHashSet();

            var matching = _files
                .Where(f => !_trashed.Contains((string)f["id"]!))
                .Where(f => f["parents"]!.AsArray().Any(p => parents.Contains((string)p!)))
                .ToList();
            var skip = int.TryParse(query["pageToken"], out var token) ? token : 0;
            var page = matching.Skip(skip).Take(Math.Min(PageSize, int.Parse(query["pageSize"] ?? "100"))).ToList();

            var json = new JsonObject { ["files"] = new JsonArray(page.Select(f => (JsonNode)f.DeepClone()).ToArray()) };
            if (skip + page.Count < matching.Count)
                json["nextPageToken"] = (skip + page.Count).ToString();

            return FakeDrive.Json(HttpStatusCode.OK, json.ToJsonString());
        }
    }
}
