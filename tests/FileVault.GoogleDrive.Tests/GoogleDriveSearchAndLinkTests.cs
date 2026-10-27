using System.Net;
using FileVault.Core;
using Google.Apis.Drive.v3;
using DriveFile = Google.Apis.Drive.v3.Data.File;
using static FileVault.GoogleDrive.Tests.FakeDrive;

namespace FileVault.GoogleDrive.Tests;

/// <summary>Search and public links against a fake Drive HTTP API — no credentials needed.</summary>
[TestFixture]
public class GoogleDriveSearchAndLinkTests
{
    private const string FilesPath = "/drive/v3/files";
    private const string NotFoundJson = """{"error":{"code":404,"message":"File not found"}}""";

    [Test]
    public async Task SearchAsync_Subfolder_KeepsDescendantsOnly()
    {
        var drive = new FakeDriveServer
        {
            SearchPages =
            {
                [""] = """
                    {"nextPageToken":"p2","files":[
                      {"id":"f1","name":"report.pdf","mimeType":"application/pdf","parents":["A"]},
                      {"id":"f2","name":"Report 2024.txt","mimeType":"text/plain","parents":["sub1"]},
                      {"id":"A","name":"Reports","mimeType":"application/vnd.google-apps.folder","parents":["ROOT"]},
                      {"id":"f4","name":"report.doc","mimeType":"text/plain","parents":["other"]}
                    ]}
                    """,
                ["p2"] = """
                    {"files":[
                      {"id":"f3","name":"reports.xlsx","mimeType":"text/plain","parents":["sub1"]},
                      {"id":"f5","name":"report shared.txt","mimeType":"text/plain"},
                      {"id":"sub2","name":"Report drafts","mimeType":"application/vnd.google-apps.folder","parents":["A"]}
                    ]}
                    """
            },
            Parents = { ["sub1"] = ["A"], ["other"] = ["ROOT"], ["ROOT"] = [] }
        };
        using var service = drive.CreateService();

        var found = await SearchAsync(new GoogleDriveFileProvider(service, "A"), "report");

        Assert.That(found.Select(i => i.FullName), Is.EqualTo(new[] { "gdrive:f1", "gdrive:f2", "gdrive:f3", "gdrive:sub2" }));
        Assert.That(found[^1], Is.InstanceOf<GoogleDriveFolderItem>());
        Assert.That(found[0], Is.InstanceOf<GoogleDriveFileItem>());
        Assert.That(drive.FolderReads, Is.EqualTo(new[] { "sub1", "other", "ROOT" }), "each ancestor is read once");
        Assert.That(drive.SearchQueries, Is.All.EqualTo("name contains 'report' and trashed = false"));
        Assert.That(drive.PageTokens, Is.EqualTo(new string?[] { null, "p2" }));
    }

    [Test]
    public async Task SearchAsync_Root_KeepsMyDriveAndDropsItemsOnlySharedWithMe()
    {
        var drive = new FakeDriveServer
        {
            SearchPages =
            {
                [""] = """
                    {"files":[
                      {"id":"f1","name":"notes.md","mimeType":"text/markdown","parents":["ROOT"]},
                      {"id":"f2","name":"notes old.md","mimeType":"text/markdown","parents":["deep"]},
                      {"id":"f3","name":"notes of a colleague.md","mimeType":"text/markdown","parents":["theirs"]}
                    ]}
                    """
            },
            Parents = { ["deep"] = ["ROOT"] },
            RootId = "ROOT"
        };
        using var service = drive.CreateService();

        var found = await SearchAsync(new GoogleDriveFileProvider(service, "root"), "notes");

        Assert.That(found.Select(i => i.FullName), Is.EqualTo(new[] { "gdrive:f1", "gdrive:f2" }));
    }

    [Test]
    public async Task SearchAsync_EscapesQuotesAndBackslashes()
    {
        var drive = new FakeDriveServer { SearchPages = { [""] = """{"files":[]}""" } };
        using var service = drive.CreateService();

        await SearchAsync(new GoogleDriveFileProvider(service, "A"), @"it's a\b");

        Assert.That(drive.SearchQueries, Is.EqualTo(new[] { @"name contains 'it\'s a\\b' and trashed = false" }));
    }

    [Test]
    public async Task SearchAsync_EmptyQuery_FindsNothingWithoutRequest()
    {
        var drive = new FakeDriveServer();
        using var service = drive.CreateService();

        Assert.That(await SearchAsync(new GoogleDriveFileProvider(service, "A"), ""), Is.Empty);
        Assert.That(drive.Requests, Is.Empty);
    }

    [Test]
    public async Task GetPublicLinkAsync_NotShared_CreatesAnyoneReader()
    {
        var drive = new FakeDriveServer { Permissions = { ["f1"] = ["""{"id":"me","type":"user","role":"owner"}"""] } };
        using var service = drive.CreateService();
        var item = new GoogleDriveFileItem(new DriveFile { Id = "f1", Name = "a.pdf" }, service);

        var result = await item.GetPublicLinkAsync();

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result, Is.EqualTo(new Uri("https://drive.google.com/file/d/f1/view")));
        Assert.That(drive.CreatedPermissions, Has.Count.EqualTo(1));
        Assert.That(drive.CreatedPermissions[0], Does.Contain("\"type\":\"anyone\"").And.Contain("\"role\":\"reader\""));
    }

    [Test]
    public async Task GetPublicLinkAsync_AnyoneAlready_ReturnsLinkWithoutChange()
    {
        var drive = new FakeDriveServer { Permissions = { ["A"] = ["""{"id":"anyoneWithLink","type":"anyone","role":"writer"}"""] } };
        using var service = drive.CreateService();
        var folder = new GoogleDriveFolderItem(
            new DriveFile { Id = "A", Name = "Docs", MimeType = "application/vnd.google-apps.folder" }, service);

        var result = await folder.GetPublicLinkAsync();

        Assert.That(result.Result, Is.EqualTo(new Uri("https://drive.google.com/file/d/A/view")));
        Assert.That(drive.CreatedPermissions, Is.Empty);
        Assert.That(drive.DeletedPermissions, Is.Empty);
    }

    [Test]
    public async Task GetPublicLinkAsync_Refused_ReturnsFailure()
    {
        using var service = CreateService(_ =>
            Json(HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"Insufficient permissions"}}"""));
        var item = new GoogleDriveFileItem(new DriveFile { Id = "f1", Name = "a.pdf" }, service);

        var result = await item.GetPublicLinkAsync();

        Assert.That(result.IsSuccess, Is.False);
    }

    [Test]
    public async Task RemovePublicLinkAsync_DeletesEveryAnyonePermission()
    {
        var drive = new FakeDriveServer
        {
            Permissions =
            {
                ["f1"] =
                [
                    """{"id":"me","type":"user","role":"owner"}""",
                    """{"id":"anyoneWithLink","type":"anyone","role":"reader"}""",
                    """{"id":"anyone","type":"anyone","role":"commenter"}""",
                    """{"id":"corp","type":"domain","role":"reader"}"""
                ]
            }
        };
        using var service = drive.CreateService();
        var item = new GoogleDriveFileItem(new DriveFile { Id = "f1", Name = "a.pdf" }, service);

        var result = await item.RemovePublicLinkAsync();

        Assert.That(result.IsSuccess && result.Result, Is.True, result.ErrorMessage);
        Assert.That(drive.DeletedPermissions, Is.EqualTo(new[] { "f1/anyoneWithLink", "f1/anyone" }));
    }

    [Test]
    public async Task RemovePublicLinkAsync_NoLink_Succeeds()
    {
        var drive = new FakeDriveServer { Permissions = { ["f1"] = ["""{"id":"me","type":"user","role":"owner"}"""] } };
        using var service = drive.CreateService();
        var item = new GoogleDriveFileItem(new DriveFile { Id = "f1", Name = "a.pdf" }, service);

        var result = await item.RemovePublicLinkAsync();

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result, Is.False);
        Assert.That(drive.DeletedPermissions, Is.Empty);
    }

    private static async Task<List<IFileProviderItem>> SearchAsync(ISearchableFileProvider provider, string query)
    {
        var found = new List<IFileProviderItem>();
        await foreach (var item in provider.SearchAsync(query))
            found.Add(item);
        return found;
    }

    /// <summary>Answers files.list (search), files.get (parents, webViewLink) and the permissions calls.</summary>
    private sealed class FakeDriveServer
    {
        public Dictionary<string, string> SearchPages { get; } = [];
        public Dictionary<string, string[]> Parents { get; } = [];
        public Dictionary<string, string[]> Permissions { get; } = [];
        public string RootId { get; init; } = "ROOT";

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> SearchQueries { get; } = [];
        public List<string?> PageTokens { get; } = [];
        public List<string> FolderReads { get; } = [];
        public List<string> CreatedPermissions { get; } = [];
        public List<string> DeletedPermissions { get; } = [];

        public DriveService CreateService() => FakeDrive.CreateService(Respond);

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);

            if (path == FilesPath)
            {
                SearchQueries.Add(query["q"]!);
                PageTokens.Add(query["pageToken"]);
                return Json(HttpStatusCode.OK, SearchPages[query["pageToken"] ?? ""]);
            }

            var segments = path[(FilesPath.Length + 1)..].Split('/');
            var id = segments[0];

            if (segments.Length == 1)
                return GetFile(id, query["fields"]);

            if (request.Method == HttpMethod.Post)
            {
                CreatedPermissions.Add(request.Content!.ReadAsStringAsync().Result);
                return Json(HttpStatusCode.OK, """{"id":"new"}""");
            }

            if (request.Method == HttpMethod.Delete)
            {
                DeletedPermissions.Add($"{id}/{segments[2]}");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var permissions = Permissions.GetValueOrDefault(id) ?? [];
            return Json(HttpStatusCode.OK, $$"""{"permissions":[{{string.Join(",", permissions)}}]}""");
        }

        private HttpResponseMessage GetFile(string id, string? fields)
        {
            if (fields == "webViewLink")
                return Json(HttpStatusCode.OK, $$"""{"webViewLink":"https://drive.google.com/file/d/{{id}}/view"}""");

            if (id == "root" && fields == "id")
                return Json(HttpStatusCode.OK, $$"""{"id":"{{RootId}}"}""");

            FolderReads.Add(id);

            if (!Parents.TryGetValue(id, out var parents))
                return Json(HttpStatusCode.NotFound, NotFoundJson);

            var list = string.Join(",", parents.Select(p => $"\"{p}\""));
            return Json(HttpStatusCode.OK, $$"""{"id":"{{id}}","parents":[{{list}}]}""");
        }
    }
}
