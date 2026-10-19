using System.Net;
using System.Text;
using FileVault.Core;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace FileVault.GoogleDrive.Tests;

/// <summary>
/// Google.Apis reports failed media transfers through Status = Failed instead of throwing.
/// These tests run DriveService against a fake HTTP handler, so they need no credentials.
/// </summary>
[TestFixture]
public class GoogleDriveTransferTests
{
    private const string ErrorJson = """{"error":{"code":403,"message":"Quota exceeded"}}""";

    [Test]
    public async Task MoveFileInAsync_UploadFails_KeepsSource()
    {
        using var service = CreateService(_ => Json(HttpStatusCode.Forbidden, ErrorJson));
        var provider = new GoogleDriveFileProvider(service, "root");
        var source = new FakeFileItem("photo.jpg", [1, 2, 3]);

        var result = await provider.MoveFileInAsync(source, "gdrive:root/photo.jpg", new Progress<double>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(source.Deleted, Is.False, "source must survive a failed upload");
    }

    [Test]
    public async Task MoveFileInAsync_UploadSucceeds_DeletesSource()
    {
        using var service = CreateService(request => request.Method == HttpMethod.Post
            ? ResumableSession()
            : Json(HttpStatusCode.OK, """{"id":"new-id","name":"photo.jpg","mimeType":"image/jpeg","size":"3"}"""));
        var provider = new GoogleDriveFileProvider(service, "root");
        var source = new FakeFileItem("photo.jpg", [1, 2, 3]);

        var result = await provider.MoveFileInAsync(source, "gdrive:root/photo.jpg", new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result!.FullName, Is.EqualTo("gdrive:new-id"));
        Assert.That(source.Deleted, Is.True);
    }

    [Test]
    public async Task CreateEmptyTextFileAsync_UploadFails_ReturnsFailure()
    {
        using var service = CreateService(_ => Json(HttpStatusCode.Forbidden, ErrorJson));
        var provider = new GoogleDriveFileProvider(service, "root");

        var result = await provider.CreateEmptyTextFileAsync("notes.txt");

        Assert.That(result.IsSuccess, Is.False);
    }

    [Test]
    public void OpenReadAsync_DownloadFails_Throws()
    {
        using var service = CreateService(_ => Json(HttpStatusCode.Forbidden, ErrorJson));
        var item = new GoogleDriveFileItem(
            new DriveFile { Id = "file-id", Name = "photo.jpg", MimeType = "image/jpeg", Size = 3 }, service);

        Assert.That(async () => await item.OpenReadAsync(), Throws.Exception);
    }

    [Test]
    public async Task OpenReadAsync_GoogleDoc_ExportsToDocx()
    {
        Uri? requested = null;
        using var service = CreateService(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7, 8, 9]) };
        });
        var item = new GoogleDriveFileItem(
            new DriveFile { Id = "doc-id", Name = "Report", MimeType = "application/vnd.google-apps.document" },
            service);

        var (stream, totalBytes) = await item.OpenReadAsync();
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(new byte[] { 7, 8, 9 }));
        }

        Assert.That(totalBytes, Is.EqualTo(3));
        Assert.That(requested!.AbsolutePath, Does.EndWith("/files/doc-id/export"));
        Assert.That(Uri.UnescapeDataString(requested.Query),
            Does.Contain("mimeType=application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    [Test]
    public void OpenReadAsync_GoogleForm_NotSupportedWithoutRequest()
    {
        var requests = 0;
        using var service = CreateService(_ =>
        {
            requests++;
            return Json(HttpStatusCode.OK, "{}");
        });
        var item = new GoogleDriveFileItem(
            new DriveFile { Id = "form-id", Name = "Survey", MimeType = "application/vnd.google-apps.form" },
            service);

        Assert.That(async () => await item.OpenReadAsync(), Throws.TypeOf<NotSupportedException>());
        Assert.That(requests, Is.Zero);
    }

    [Test]
    public async Task CopyFileInAsync_OtherAccount_DownloadsAndUploads()
    {
        var targetRequests = new List<HttpRequestMessage>();
        using var sourceService = CreateService(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var targetService = CreateService(request =>
        {
            targetRequests.Add(request);
            return request.Method == HttpMethod.Post
                ? ResumableSession()
                : Json(HttpStatusCode.OK, """{"id":"new-id","name":"photo.jpg","mimeType":"image/jpeg","size":"3"}""");
        });
        var provider = new GoogleDriveFileProvider(targetService, "root");
        var source = new GoogleDriveFileItem(
            new DriveFile { Id = "src-id", Name = "photo.jpg", MimeType = "image/jpeg", Size = 3 }, sourceService);

        var result = await provider.CopyFileInAsync(source, "gdrive:root/photo.jpg", new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(targetRequests.Select(r => r.RequestUri!.AbsolutePath),
            Has.None.Contains("/copy"), "server-side copy would look up src-id in the wrong account");
        Assert.That(targetRequests.Select(r => r.RequestUri!.AbsolutePath), Has.Some.StartsWith("/upload/"));
    }

    [Test]
    public async Task TryMoveFolderAsync_OtherAccount_LetsOrchestratorRecurse()
    {
        var targetRequests = 0;
        using var sourceService = CreateService(_ => Json(HttpStatusCode.OK, "{}"));
        using var targetService = CreateService(_ =>
        {
            targetRequests++;
            return Json(HttpStatusCode.OK, "{}");
        });
        var provider = new GoogleDriveFileProvider(targetService, "root");
        var folder = new GoogleDriveFolderItem(
            new DriveFile { Id = "folder-id", Name = "Photos", MimeType = "application/vnd.google-apps.folder" },
            sourceService);

        var result = await provider.TryMoveFolderAsync(folder, "gdrive:root/Photos", new Progress<double>());

        Assert.That(result.IsSuccess && result.Result is null, Is.True);
        Assert.That(targetRequests, Is.Zero);
    }

    [Test]
    public async Task Revisions_AllPages_OldestFirst()
    {
        using var service = CreateService(request => request.RequestUri!.Query.Contains("pageToken=next")
            ? Json(HttpStatusCode.OK, """{"revisions":[{"id":"r1","modifiedTime":"2026-10-01T10:00:00Z","size":"2","md5Checksum":"aa"}]}""")
            : Json(HttpStatusCode.OK, """{"nextPageToken":"next","revisions":[{"id":"r2","modifiedTime":"2026-10-02T10:00:00Z","size":"3","md5Checksum":"bb","lastModifyingUser":{"displayName":"Anna"}}]}"""));
        var item = new GoogleDriveFileItem(
            new DriveFile { Id = "file-id", Name = "notes.md", MimeType = "text/markdown", Size = 3 }, service);

        var revisions = await item.GetRevisionsAsync();

        Assert.That(revisions.Select(r => (r.Id, r.Size, r.Md5, r.ModifiedBy)), Is.EqualTo(new[]
        {
            ("r1", (long?)2, "aa", (string?)null),
            ("r2", (long?)3, "bb", "Anna")
        }));
    }

    [Test]
    public async Task Revisions_GoogleDoc_NoneWithoutRequest()
    {
        var requests = 0;
        using var service = CreateService(_ =>
        {
            requests++;
            return Json(HttpStatusCode.OK, "{}");
        });
        var item = new GoogleDriveFileItem(
            new DriveFile { Id = "doc-id", Name = "Report", MimeType = "application/vnd.google-apps.document" }, service);

        Assert.That(await item.GetRevisionsAsync(), Is.Empty);
        Assert.That(requests, Is.Zero);
    }

    [Test]
    public async Task OpenRevision_DownloadsThatRevision()
    {
        Uri? requested = null;
        using var service = CreateService(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5]) };
        });
        var item = new GoogleDriveFileItem(
            new DriveFile { Id = "file-id", Name = "notes.md", MimeType = "text/markdown", Size = 3 }, service);

        var (stream, length) = await item.OpenRevisionAsync(new FileRevision("r1", DateTimeOffset.Now, 2, null, null));
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(new byte[] { 4, 5 }));
        }

        Assert.That(length, Is.EqualTo(2));
        Assert.That(requested!.AbsolutePath, Does.EndWith("/files/file-id/revisions/r1"));
        Assert.That(requested.Query, Does.Contain("alt=media"));
    }

    [Test]
    public async Task Find_WalksNames_FoldersOnTheWay_NewestOfDuplicates()
    {
        var queries = new List<string>();
        using var service = CreateService(request =>
        {
            var q = Uri.UnescapeDataString(request.RequestUri!.Query).Replace('+', ' ');
            queries.Add(q);

            return q.Contains("name = 'Projects'")
                ? Json(HttpStatusCode.OK, """{"files":[{"id":"file-named-projects","name":"Projects","mimeType":"text/plain"},{"id":"projects-id","name":"Projects","mimeType":"application/vnd.google-apps.folder"}]}""")
                : q.Contains(@"name = 'Ann\'s.md'")
                    ? Json(HttpStatusCode.OK, """{"files":[{"id":"newest","name":"Ann's.md","mimeType":"text/markdown"},{"id":"older","name":"Ann's.md","mimeType":"text/markdown"}]}""")
                    : Json(HttpStatusCode.OK, """{"files":[]}""");
        });
        var provider = new GoogleDriveFileProvider(service, "root");

        var found = await provider.FindAsync("Projects/Ann's.md");
        var missing = await provider.FindAsync("Projects/none.md");

        Assert.That(found, Is.TypeOf<GoogleDriveFileItem>());
        Assert.That(found!.FullName, Is.EqualTo("gdrive:newest"));
        Assert.That(queries[1], Does.Contain("'projects-id' in parents"));
        Assert.That(queries[0], Does.Contain("orderBy=modifiedTime desc"));
        Assert.That(missing, Is.Null);
    }

    private static DriveService CreateService(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new BaseClientService.Initializer
        {
            HttpClientFactory = new FakeHttpClientFactory(new FakeHandler(respond)),
            ApplicationName = "FileVault.Tests",
            GZipEnabled = false
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ResumableSession()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        response.Headers.Location = new Uri("https://www.googleapis.com/upload/drive/v3/files?upload_id=session");
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private sealed class FakeFileItem(string name, byte[] content) : IFileItem
    {
        public bool Deleted { get; private set; }

        public string Name => name;
        public string FullName => $"fake:{name}";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        long? IFileProviderItem.Size => Size;
        public long Size => content.Length;
        public string Extension => Path.GetExtension(name);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
            => Task.FromResult<(Stream, long)>((new MemoryStream(content), content.Length));

        public Task DeleteAsync(CancellationToken ct = default)
        {
            Deleted = true;
            return Task.CompletedTask;
        }
    }
}
