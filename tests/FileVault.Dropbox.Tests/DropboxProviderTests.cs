using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FileVault.Core;

namespace FileVault.Dropbox.Tests;

[TestFixture]
public class DropboxProviderTests
{
    private FakeDropbox _dropbox = null!;
    private DropboxClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _dropbox = new FakeDropbox();
        _client = _dropbox.CreateClient(new DropboxClientOptions
        {
            JobPollInterval = TimeSpan.Zero,
            DefaultRetryAfter = TimeSpan.Zero
        });
    }

    [TearDown]
    public void TearDown() => _dropbox.Dispose();

    // ── Listing ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Listing_FollowsCursorPages_FoldersFirst()
    {
        _dropbox.ListPageSize = 2;
        _dropbox.AddFile("/a.txt", "a");
        _dropbox.AddFolder("/B");
        _dropbox.AddFile("/c.txt", "c");
        _dropbox.AddFolder("/D");
        _dropbox.AddFile("/.hidden", "h");

        var items = await _client.CreateProvider().GetItemsAsync(FileProviderFilter.Default).ToListAsync();

        Assert.That(items.Select(i => i.Name), Is.EqualTo(new[] { "B", "D", "a.txt", "c.txt" }));
        Assert.That(_dropbox.Routes.Count(r => r == "files/list_folder/continue"), Is.EqualTo(2));
        Assert.That(items.OfType<IFolderItem>().All(f => f.ChangedDate == default), "Dropbox keeps no folder dates");
    }

    [Test]
    public async Task FullName_KeepsDisplayCasing_AndParsesBack()
    {
        _dropbox.AddFile("/Photos/Summer 2024/IMG_1.JPG", "x");

        var folder = (IFolderItem)(await _client.CreateProvider("dropbox:/photos").GetItemsAsync(FileProviderFilter.ShowAll)
            .ToListAsync()).Single();
        var file = (await folder.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();

        Assert.That(folder.FullName, Is.EqualTo("dropbox:/Photos/Summer 2024"));
        Assert.That(file.FullName, Is.EqualTo("dropbox:/Photos/Summer 2024/IMG_1.JPG"));
        Assert.That(DropboxPath.TryParse(file.FullName, out var path), Is.True);
        Assert.That(path, Is.EqualTo("/Photos/Summer 2024/IMG_1.JPG"));
        Assert.That(DropboxPath.Format(""), Is.EqualTo("dropbox:/"));
        Assert.That(DropboxPath.TryParse("dropbox:/", out var root) && root == "", Is.True);
        Assert.That(DropboxPath.TryParse("dropbox:/a/../b", out _), Is.False);
        Assert.That(DropboxPath.TryParse("disk:/a", out _), Is.False);
    }

    [Test]
    public async Task Contains_IsCaseInsensitive_AndChecksKind()
    {
        _dropbox.AddFile("/Docs/Report.PDF", "x");
        var provider = _client.CreateProvider("/Docs");

        Assert.That(await provider.ContainsFileAsync("report.pdf"), Is.True);
        Assert.That(await provider.ContainsFolderAsync("report.pdf"), Is.False);
        Assert.That(await provider.ContainsFileAsync("other.pdf"), Is.False);
    }

    [Test]
    public void Contains_NetworkError_Throws()
    {
        _dropbox.RateLimitedRequests = 10;
        var provider = _client.CreateProvider();

        Assert.ThrowsAsync<DropboxApiException>(() => provider.ContainsFileAsync("a.txt"));
    }

    [Test]
    public async Task ResolveDestination_CountsNamesCaseInsensitively()
    {
        _dropbox.AddFile("/REPORT.pdf", "x");
        var source = new FakeSource("report.pdf", []);

        var resolved = await _client.CreateProvider().ResolveDestinationFileAsync(source, overwrite: false);

        Assert.That(resolved.Name, Is.EqualTo("report (2).pdf"));
        Assert.That(resolved.FullName, Is.EqualTo("dropbox:/report (2).pdf"));
    }

    // ── Upload ──────────────────────────────────────────────────────────────

    [Test]
    public async Task SmallSeekableUpload_IsOneRequest()
    {
        var progress = new RecordingProgress();
        var result = await _client.CreateProvider().CopyFileInAsync(new FakeSource("a.bin", [1, 2, 3]), "dropbox:/a.bin",
            progress);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_dropbox.Read("/a.bin"), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(_dropbox.Routes.Where(r => r.StartsWith("files/upload")), Is.EqualTo(new[] { "files/upload" }));
        Assert.That(progress.Values[^1], Is.EqualTo(1));
        Assert.That(result.Result!.Size, Is.EqualTo(3));
    }

    [Test]
    public async Task LargeUpload_GoesThroughSession_InChunks()
    {
        var client = _dropbox.CreateClient(new DropboxClientOptions { SimpleUploadLimit = 10, UploadChunkSize = 8 });
        var content = Enumerable.Range(0, 25).Select(i => (byte)i).ToArray();
        var progress = new RecordingProgress();

        var result = await client.CreateProvider().CopyFileInAsync(new FakeSource("big.bin", content), "dropbox:/big.bin",
            progress);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_dropbox.Read("/big.bin"), Is.EqualTo(content));

        var uploads = _dropbox.Requests.Where(r => r.Route.StartsWith("files/upload")).ToList();
        Assert.That(uploads.Select(r => r.Route), Is.EqualTo(new[]
        {
            "files/upload_session/start", "files/upload_session/append_v2", "files/upload_session/append_v2",
            "files/upload_session/finish"
        }));
        Assert.That(uploads.Select(r => r.BodyLength), Is.EqualTo(new long[] { 8, 8, 8, 1 }));
        Assert.That(uploads[2].Arg, Does.Contain("\"offset\":16"));
        Assert.That(uploads[3].Arg, Does.Contain("\"mode\":\"overwrite\"").And.Contain("\"autorename\":false"));
        Assert.That(progress.Values, Is.Ordered);
        Assert.That(progress.Values[^1], Is.EqualTo(1));
    }

    [Test]
    public async Task SessionUpload_ExactMultipleOfChunk_FinishesWithEmptyBody()
    {
        var client = _dropbox.CreateClient(new DropboxClientOptions { SimpleUploadLimit = 0, UploadChunkSize = 4 });
        var content = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();

        var result = await client.CreateProvider().CopyFileInAsync(new FakeSource("b.bin", content), "dropbox:/b.bin",
            new RecordingProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_dropbox.Read("/b.bin"), Is.EqualTo(content));
        Assert.That(_dropbox.Requests.Where(r => r.Route.StartsWith("files/upload")).Select(r => r.BodyLength),
            Is.EqualTo(new long[] { 4, 4, 0 }));
    }

    [Test]
    public async Task NonSeekableSource_UsesSession_EvenWhenSmall()
    {
        var client = _dropbox.CreateClient(new DropboxClientOptions { UploadChunkSize = 4 });
        var content = Enumerable.Range(0, 6).Select(i => (byte)i).ToArray();

        var result = await client.CreateProvider().CopyFileInAsync(new FakeSource("c.bin", content, seekable: false),
            "dropbox:/c.bin", new RecordingProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_dropbox.Read("/c.bin"), Is.EqualTo(content));
        Assert.That(_dropbox.Routes.Where(r => r.StartsWith("files/upload")),
            Is.EqualTo(new[] { "files/upload_session/start", "files/upload_session/finish" }));
    }

    [Test]
    public async Task CancelledUpload_LeavesNoFile()
    {
        var client = _dropbox.CreateClient(new DropboxClientOptions { SimpleUploadLimit = 0, UploadChunkSize = 4 });
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress(_ => cts.Cancel());

        var result = await client.CreateProvider().CopyFileInAsync(new FakeSource("d.bin", new byte[20]), "dropbox:/d.bin",
            progress, cts.Token);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.InstanceOf<OperationCanceledException>());
        Assert.That(_dropbox.Read("/d.bin"), Is.Null);
        Assert.That(_dropbox.Routes, Does.Not.Contain("files/upload_session/finish"));
    }

    [Test]
    public async Task Upload_ReplacesExistingFile()
    {
        _dropbox.AddFile("/a.txt", "old");

        var result = await _client.CreateProvider().CopyFileInAsync(new FakeSource("a.txt", "new"u8.ToArray()),
            "dropbox:/a.txt", new RecordingProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(Encoding.UTF8.GetString(_dropbox.Read("/a.txt")!), Is.EqualTo("new"));
    }

    [Test]
    public async Task CreateEmptyTextFile_UsesAddMode()
    {
        var result = await _client.CreateProvider().CreateEmptyTextFileAsync("note.txt");

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_dropbox.Requests.Single(r => r.Route == "files/upload").Arg, Does.Contain("\"mode\":\"add\""));
    }

    // ── Dropbox-API-Arg ─────────────────────────────────────────────────────

    [Test]
    public void ApiArg_EscapesEverythingOutsideAscii()
    {
        var header = DropboxApi.EncodeArg(new ApiPathArg("/Документы/naïve 😀\u007f.txt"), DropboxJsonContext.Default.ApiPathArg);

        Assert.That(header.All(c => c is >= ' ' and < (char)0x7F), Is.True, header);
        Assert.That(header, Does.Contain("\\u0414").IgnoreCase.And.Contain("\\ud83d\\ude00").IgnoreCase);
        Assert.That((string?)JsonNode.Parse(header)!["path"], Is.EqualTo("/Документы/naïve 😀\u007f.txt"));
    }

    [Test]
    public async Task NonAsciiNames_RoundTripThroughContentEndpoints()
    {
        const string name = "Привет, мир 😀.txt";

        var upload = await _client.CreateProvider("/Папка").CopyFileInAsync(new FakeSource(name, "hi"u8.ToArray()),
            $"dropbox:/Папка/{name}", new RecordingProgress());
        Assert.That(upload.IsSuccess, Is.True, upload.ErrorMessage);
        Assert.That(_dropbox.Read($"/Папка/{name}"), Is.EqualTo("hi"u8.ToArray()));

        var file = (IFileItem)(await _client.CreateProvider("/Папка").GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();
        var (stream, length) = await file.OpenReadAsync();
        await using (stream)
        {
            Assert.That(length, Is.EqualTo(2));
            Assert.That(file.FullName, Is.EqualTo($"dropbox:/Папка/{name}"));
        }

        Assert.That(_dropbox.Requests.Where(r => r.Arg is not null).All(r => r.Arg!.All(c => c < 0x7F)), Is.True);
    }

    // ── Auth and rate limits ────────────────────────────────────────────────

    [Test]
    public async Task ExpiredToken_IsRefreshedOnce_AndTheCallRepeated()
    {
        _dropbox.ExpiredTokens.Add("old");
        _dropbox.ValidTokens.Add("new");
        var source = new SwitchingTokenSource("old", "new");
        var client = new DropboxClient(source, new HttpClient(_dropbox));
        _dropbox.AddFile("/a.txt", "a");

        var items = await client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(source.Rejected, Is.EqualTo(new[] { "old" }));
        Assert.That(_dropbox.Requests.Select(r => r.Token), Is.EqualTo(new[] { "old", "new" }));
    }

    [Test]
    public void ExpiredToken_SourceReturnsSameToken_FailsWithoutLooping()
    {
        _dropbox.ExpiredTokens.Add("old");
        var client = new DropboxClient(_ => ValueTask.FromResult("old"), new HttpClient(_dropbox));

        var error = Assert.ThrowsAsync<DropboxApiException>(() => client.GetSpaceUsageAsync());

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(error.Tag, Is.EqualTo("expired_access_token"));
        Assert.That(_dropbox.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RateLimit_IsWaitedOut_WithinTheBound()
    {
        _dropbox.RateLimitedRequests = 2;
        _dropbox.RetryAfterSeconds = 0;

        var usage = await _client.GetSpaceUsageAsync();

        Assert.That(usage.Allocated, Is.EqualTo(_dropbox.Quota));
        Assert.That(_dropbox.Requests, Has.Count.EqualTo(3));
    }

    [Test]
    public void RateLimit_BeyondTheBound_Fails()
    {
        _dropbox.RateLimitedRequests = 10;

        var error = Assert.ThrowsAsync<DropboxApiException>(() => _client.GetSpaceUsageAsync());

        Assert.That(error!.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(_dropbox.Requests, Has.Count.EqualTo(1 + _client.Options.MaxRateLimitRetries));
    }

    [Test]
    public void RateLimit_LongerThanMaxRetryAfter_FailsAtOnce()
    {
        _dropbox.RateLimitedRequests = 1;
        _dropbox.RetryAfterSeconds = 300;

        var error = Assert.ThrowsAsync<DropboxApiException>(() => _client.GetSpaceUsageAsync());

        Assert.That(error!.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(300)));
        Assert.That(_dropbox.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RateLimitedUpload_SendsTheBodyAgain()
    {
        _dropbox.RateLimitedRequests = 1;

        var result = await _client.CreateProvider().CopyFileInAsync(new FakeSource("a.bin", [9, 8, 7]), "dropbox:/a.bin",
            new RecordingProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_dropbox.Read("/a.bin"), Is.EqualTo(new byte[] { 9, 8, 7 }));
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    [Test]
    public async Task DeleteToRecycleBin_UsesDeleteV2()
    {
        _dropbox.AddFile("/a.txt", "a");
        var items = await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await _client.CreateProvider().DeleteAsync(items, toRecycleBin: true);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_dropbox.DeletedPaths, Is.EqualTo(new[] { "/a.txt" }));
        Assert.That(_dropbox.Routes, Does.Not.Contain("files/permanently_delete"));
    }

    [Test]
    public async Task PermanentDelete_RefusedOutsideBusiness_FailsAndKeepsTheFile()
    {
        _dropbox.PermanentDeleteAllowed = false;
        _dropbox.AddFile("/a.txt", "a");
        _dropbox.AddFile("/b.txt", "b");
        var items = await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await _client.CreateProvider().DeleteAsync(items, toRecycleBin: false);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.InstanceOf<DropboxApiException>());
        Assert.That(_dropbox.Read("/a.txt"), Is.Not.Null);
        Assert.That(_dropbox.Read("/b.txt"), Is.Not.Null);
        Assert.That(_dropbox.Routes, Does.Not.Contain("files/delete_v2"));
    }

    [Test]
    public async Task Delete_ItemOfAnotherAccount_IsRefusedBeforeTouchingAny()
    {
        _dropbox.AddFile("/a.txt", "a");
        var own = await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var other = await _dropbox.CreateClient().CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await _client.CreateProvider().DeleteAsync([own[0], other[0]], toRecycleBin: true);

        Assert.That(result.Exception, Is.InstanceOf<ArgumentException>());
        Assert.That(_dropbox.DeletedPaths, Is.Empty);
    }

    // ── Copy, move, rename ──────────────────────────────────────────────────

    [Test]
    public async Task SameAccountCopyAndMove_StayOnDropbox()
    {
        _dropbox.AddFile("/src/a.txt", "a");
        var file = (IFileItem)(await _client.CreateProvider("/src").GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();
        var destination = _client.CreateProvider("/dst");

        var copy = await destination.CopyFileInAsync(file, "dropbox:/dst/a.txt", new RecordingProgress());
        var move = await destination.MoveFileInAsync(file, "dropbox:/dst/b.txt", new RecordingProgress());

        Assert.That(copy.IsSuccess && move.IsSuccess, Is.True, copy.ErrorMessage ?? move.ErrorMessage);
        Assert.That(_dropbox.Read("/dst/a.txt"), Is.Not.Null);
        Assert.That(_dropbox.Read("/dst/b.txt"), Is.Not.Null);
        Assert.That(_dropbox.Read("/src/a.txt"), Is.Null);
        Assert.That(_dropbox.Routes, Does.Contain("files/copy_v2").And.Contain("files/move_v2"));
        Assert.That(_dropbox.Routes, Does.Not.Contain("files/download").And.Not.Contain("files/upload"));
    }

    [Test]
    public async Task SameAccountCopy_OntoExistingFile_ReplacesIt()
    {
        _dropbox.AddFile("/a.txt", "new");
        _dropbox.AddFile("/dst/a.txt", "old");
        var file = (IFileItem)(await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync())
            .Single(i => i.Name == "a.txt");

        var copy = await _client.CreateProvider("/dst").CopyFileInAsync(file, "dropbox:/dst/a.txt", new RecordingProgress());

        Assert.That(copy.IsSuccess, Is.True, copy.ErrorMessage);
        Assert.That(Encoding.UTF8.GetString(_dropbox.Read("/dst/a.txt")!), Is.EqualTo("new"));
        Assert.That(_dropbox.DeletedPaths, Is.EqualTo(new[] { "/dst/a.txt" }), "the old file goes to the deleted files");
    }

    [Test]
    public async Task CrossAccountCopy_StreamsThroughDownloadAndUpload()
    {
        var other = new FakeDropbox();
        other.AddFile("/x.bin", [5, 6]);
        var file = (IFileItem)(await other.CreateClient().CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();

        var copy = await _client.CreateProvider().CopyFileInAsync(file, "dropbox:/x.bin", new RecordingProgress());

        Assert.That(copy.IsSuccess, Is.True, copy.ErrorMessage);
        Assert.That(_dropbox.Read("/x.bin"), Is.EqualTo(new byte[] { 5, 6 }));
        Assert.That(other.Routes, Does.Contain("files/download"));
        Assert.That(_dropbox.Routes, Does.Not.Contain("files/copy_v2"));
    }

    [Test]
    public async Task FolderCopy_TooManyFiles_RunsAsBatchJob()
    {
        _dropbox.TooManyFilesLimit = 2;
        _dropbox.JobPolls = 2;
        _dropbox.AddFile("/Big/1.txt", "1");
        _dropbox.AddFile("/Big/2.txt", "2");
        _dropbox.AddFile("/Big/sub/3.txt", "3");
        var folder = (IFolderItem)(await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();

        var copy = await _client.CreateProvider().CopyFolderInAsync(folder, "dropbox:/Copy", new RecordingProgress());

        Assert.That(copy.IsSuccess, Is.True, copy.ErrorMessage);
        Assert.That(copy.Result!.FullName, Is.EqualTo("dropbox:/Copy"));
        Assert.That(_dropbox.Read("/Copy/sub/3.txt"), Is.EqualTo("3"u8.ToArray()));
        Assert.That(_dropbox.Routes.Count(r => r == "files/copy_batch/check_v2"), Is.EqualTo(3));
    }

    [Test]
    public async Task FolderMove_IntoExistingFolder_LetsTheCallerMerge()
    {
        _dropbox.AddFile("/A/1.txt", "1");
        _dropbox.AddFolder("/B/A");
        var folder = (IFolderItem)(await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync())
            .Single(i => i.Name == "A");

        var move = await _client.CreateProvider("/B").TryMoveFolderAsync(folder, "dropbox:/B/A", new RecordingProgress());

        Assert.That(move.IsSuccess, Is.True, move.ErrorMessage);
        Assert.That(move.Result, Is.Null);
        Assert.That(_dropbox.Read("/A/1.txt"), Is.Not.Null);
    }

    [Test]
    public async Task FolderCopy_IntoItself_Fails()
    {
        _dropbox.AddFolder("/A");
        var folder = (IFolderItem)(await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();

        var copy = await _client.CreateProvider("/A").CopyFolderInAsync(folder, "dropbox:/A/A", new RecordingProgress());

        Assert.That(copy.IsSuccess, Is.False);
    }

    [Test]
    public async Task CaseOnlyRename_GoesThroughATemporaryName()
    {
        _dropbox.AddFile("/readme.md", "x");
        var file = (await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();

        var result = await _client.CreateProvider().RenameAsync(file, "README.md");

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_dropbox.DisplayPath("/readme.md"), Is.EqualTo("/README.md"));
        Assert.That(_dropbox.Routes.Count(r => r == "files/move_v2"), Is.EqualTo(2));
    }

    [Test]
    public async Task Rename_OntoAnotherItem_Fails()
    {
        _dropbox.AddFile("/a.txt", "a");
        _dropbox.AddFile("/b.txt", "b");
        var file = (await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).First(i => i.Name == "a.txt");

        var result = await _client.CreateProvider().RenameAsync(file, "B.TXT");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(((DropboxApiException)result.Exception!).HasPath("to/conflict/file"), Is.True);
        Assert.That(_dropbox.Read("/a.txt"), Is.Not.Null);
    }

    // ── Quota, thumbnails, search, links ────────────────────────────────────

    [Test]
    public async Task Drive_CarriesQuota()
    {
        _dropbox.Quota = 1000;
        _dropbox.AddFile("/a.bin", new byte[300]);

        var drive = await _client.GetDriveAsync();

        Assert.That(drive.FullName, Is.EqualTo("dropbox:/"));
        Assert.That(drive.TotalSize, Is.EqualTo(1000));
        Assert.That(drive.TotalFreeSpace, Is.EqualTo(700));
        Assert.That(await drive.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Thumbnail_AsksForTheNearestLargerSize()
    {
        _dropbox.AddFile("/p.jpg", "jpeg");
        _dropbox.AddFile("/n.txt", "text");
        var items = (await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync())
            .Cast<IThumbnailItem>().ToDictionary(i => i.Name);

        Assert.That(items["n.txt"].HasThumbnail, Is.False);
        Assert.That(await items["n.txt"].OpenThumbnailAsync(256), Is.Null);
        Assert.That(items["p.jpg"].HasThumbnail, Is.True);

        await using var thumbnail = await items["p.jpg"].OpenThumbnailAsync(300);
        Assert.That(new StreamReader(thumbnail!).ReadToEnd(), Is.EqualTo("JPEG w480h320 bestfit jpeg p.jpg"));
        Assert.That(DropboxClient.ThumbnailSize(10), Is.EqualTo("w32h32"));
        Assert.That(DropboxClient.ThumbnailSize(5000), Is.EqualTo("w2048h1536"));
    }

    [Test]
    public async Task Thumbnail_RefusedByDropbox_IsNull()
    {
        _dropbox.AddFile("/clip.mp4", "video");
        var item = (IThumbnailItem)(await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();

        Assert.That(item.HasThumbnail, Is.True);
        Assert.That(await item.OpenThumbnailAsync(128), Is.Null);
    }

    [Test]
    public async Task Search_PagesAndStaysBelowTheFolder()
    {
        _dropbox.SearchPageSize = 2;
        _dropbox.AddFile("/Work/report-1.txt", "1");
        _dropbox.AddFile("/Work/sub/Report-2.txt", "2");
        _dropbox.AddFile("/Work/sub/report-3.txt", "3");
        _dropbox.AddFile("/Home/report-4.txt", "4");

        var provider = (ISearchableFileProvider)_client.CreateProvider("/Work");
        var found = await provider.SearchAsync("report").ToListAsync();

        Assert.That(found.Select(i => i.FullName), Is.EquivalentTo(new[]
        {
            "dropbox:/Work/report-1.txt", "dropbox:/Work/sub/Report-2.txt", "dropbox:/Work/sub/report-3.txt"
        }));
        Assert.That(_dropbox.Routes, Does.Contain("files/search/continue_v2"));
    }

    [Test]
    public async Task PublicLink_CreatedViewOnly_ThenReused_ThenRemoved()
    {
        _dropbox.AddFile("/a.txt", "a");
        var item = (IPublicLinkItem)(await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();

        var first = await item.GetPublicLinkAsync();
        var again = await item.GetPublicLinkAsync();

        Assert.That(first.IsSuccess, Is.True, first.ErrorMessage);
        Assert.That(again.Result, Is.EqualTo(first.Result));
        Assert.That((string?)_dropbox.LastLinkSettings!["audience"], Is.EqualTo("public"));
        Assert.That((string?)_dropbox.LastLinkSettings!["access"], Is.EqualTo("viewer"));

        var removed = await item.RemovePublicLinkAsync();
        var removedAgain = await item.RemovePublicLinkAsync();

        Assert.That(removed.IsSuccess && removedAgain.IsSuccess, Is.True);
        Assert.That(_dropbox.HasLink("/a.txt"), Is.False);
    }

    [Test]
    public async Task PublicLink_AlreadyExistsWithoutMetadata_IsFoundThroughList()
    {
        _dropbox.ExistingLinkInError = false;
        _dropbox.AddFolder("/Shared");
        var item = (IPublicLinkItem)(await _client.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();

        var first = await item.GetPublicLinkAsync();
        var again = await item.GetPublicLinkAsync();

        Assert.That(again.IsSuccess, Is.True, again.ErrorMessage);
        Assert.That(again.Result, Is.EqualTo(first.Result));
        Assert.That(_dropbox.Routes, Does.Contain("sharing/list_shared_links"));
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class FakeSource(string name, byte[] content, bool seekable = true) : IFileItem
    {
        public string Name => name;
        public string FullName => $"fake:/{name}";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        public long Size => content.Length;
        long? IFileProviderItem.Size => content.Length;
        public string Extension => Path.GetExtension(name);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
            Task.FromResult<(Stream, long)>((seekable ? new MemoryStream(content) : new ForwardOnlyStream(content), content.Length));

        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ForwardOnlyStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 3));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingProgress(Action<double>? onReport = null) : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value)
        {
            Values.Add(value);
            onReport?.Invoke(value);
        }
    }

    private sealed class SwitchingTokenSource(string first, string second) : IDropboxTokenSource
    {
        public List<string> Rejected { get; } = [];

        public ValueTask<string> GetAccessTokenAsync(string? rejectedToken, CancellationToken ct = default)
        {
            if (rejectedToken is null)
                return ValueTask.FromResult(Rejected.Count == 0 ? first : second);

            Rejected.Add(rejectedToken);
            return ValueTask.FromResult(second);
        }
    }
}
