using System.Net;
using System.Text;
using FileVault.Core;

namespace FileVault.OneDrive.Tests;

[TestFixture]
public class OneDriveClientTests
{
    private FakeGraph _graph = null!;
    private List<TimeSpan> _delays = null!;
    private OneDriveClient _client = null!;
    private string _folderId = null!;
    private OneDriveFileProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _graph = new FakeGraph();
        _delays = [];
        _client = _graph.CreateClient(delays: _delays);
        _folderId = _graph.AddFolder(FakeGraph.RootId, "Work");
        _provider = _client.CreateProvider(_folderId, "Work");
    }

    [TearDown]
    public void TearDown() => _graph.Dispose();

    private OneDriveFileProvider ProviderWith(OneDriveClientOptions options) =>
        _graph.CreateClient(options, _delays).CreateProvider(_folderId, "Work");

    private async Task<T> ItemAsync<T>(string name) where T : IFileProviderItem =>
        (await _provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).OfType<T>().Single(i => i.Name == name);

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

    // ── Listing ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Listing_FollowsNextLinkPages_FoldersFirst()
    {
        _graph.AddFile(_folderId, "a.txt", [1]);
        _graph.AddFolder(_folderId, "B");
        _graph.AddFile(_folderId, "c.txt", [2]);
        _graph.AddFolder(_folderId, "D");
        _graph.AddFile(_folderId, ".hidden", [3]);

        var provider = ProviderWith(new OneDriveClientOptions { PageSize = 2 });
        var all = await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var visible = await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync();

        Assert.That(all.Select(i => i.Name), Is.EqualTo(new[] { "B", "D", "a.txt", "c.txt", ".hidden" }));
        Assert.That(visible.Select(i => i.Name), Does.Not.Contain(".hidden"));
        Assert.That(_graph.Requests.Count(r => r.Request.Contains("/children") && r.Request.Contains("skiptoken")),
            Is.EqualTo(4), "two listings of three pages each: two follow-up pages per listing");
        Assert.That(all.OfType<IFileItem>().First().FullName, Does.StartWith("onedrive:ITEM!"));
    }

    [Test]
    public async Task Drive_HasQuotaAndRootFullName()
    {
        _graph.AddFile(_folderId, "a.bin", new byte[1000]);

        var drive = await _client.GetDriveAsync();

        Assert.That(drive.FullName, Is.EqualTo("onedrive:root"));
        Assert.That(drive.DriveType, Is.EqualTo("personal"));
        Assert.That(drive.TotalSize, Is.EqualTo(_graph.QuotaTotal));
        Assert.That(drive.TotalFreeSpace, Is.EqualTo(_graph.QuotaTotal - 1000));
        Assert.That((await drive.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single().Name,
            Is.EqualTo("Work"));
    }

    [Test]
    public async Task FindAndGetItem_ByPathAndId()
    {
        var sub = _graph.AddFolder(_folderId, "Sub Folder");
        var id = _graph.AddFile(sub, "notes #1.md", [1, 2]);

        var found = await _provider.FindAsync("Sub Folder/notes #1.md");
        var byId = await _client.GetItemAsync(id);

        Assert.That(found?.FullName, Is.EqualTo($"onedrive:{id}"));
        Assert.That(byId, Is.InstanceOf<OneDriveFileItem>());
        Assert.That(await _provider.FindAsync("Sub Folder/missing.md"), Is.Null);
        Assert.That(await _client.GetItemAsync("ITEM!404"), Is.Null);
        Assert.That(OneDriveClient.TryParseFullName($"onedrive:{id}", out var parsed) && parsed == id, Is.True);
        Assert.That(OneDriveClient.TryParseFullName($"onedrive:{sub}/x.md", out _), Is.False);
    }

    [Test]
    public async Task Contains_IsCaseInsensitive_AndNamesAreSharedByFilesAndFolders()
    {
        _graph.AddFolder(_folderId, "Report");

        Assert.That(await _provider.ContainsFolderAsync("report"), Is.True);
        Assert.That(await _provider.ContainsFileAsync("Report"), Is.False);

        // A file cannot take a folder's name in OneDrive
        var dest = await _provider.ResolveDestinationFileAsync(new BytesFileItem("Report", []), overwrite: false);
        Assert.That(dest.Name, Is.EqualTo("Report (2)"));
        Assert.That(dest.FullName, Is.EqualTo($"onedrive:{_folderId}/Report (2)"));
    }

    // ── Upload ──────────────────────────────────────────────────────────────

    [Test]
    public async Task SmallFile_IsOnePut()
    {
        var result = await _provider.CopyFileInAsync(new BytesFileItem("small.bin", Pattern(1000)),
            $"onedrive:{_folderId}/small.bin", new NullProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_graph.Find(_folderId, "small.bin")!.Content, Is.EqualTo(Pattern(1000)));
        Assert.That(_graph.UploadSessionsCreated, Is.Zero);
        Assert.That(_graph.Requests.Single(r => r.Request.StartsWith("PUT")).Request,
            Does.Contain(":/small.bin:/content?@microsoft.graph.conflictBehavior=replace"));
    }

    [Test]
    public async Task LargeFile_GoesInFragmentsOf320KiB_WithoutToken()
    {
        const int unit = OneDriveClientOptions.UploadFragmentUnit;
        var content = Pattern(3 * unit + 12_345);
        var provider = ProviderWith(new OneDriveClientOptions { SimpleUploadLimit = 100_000, UploadChunkSize = unit });
        var reports = new List<double>();

        var result = await provider.CopyFileInAsync(new BytesFileItem("big.bin", content), $"onedrive:{_folderId}/big.bin",
            new SyncProgress(reports.Add));

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_graph.Find(_folderId, "big.bin")!.Content.AsSpan().SequenceEqual(content), Is.True);
        Assert.That(_graph.Fragments, Is.EqualTo(new[]
        {
            (0L, unit - 1L, (long)content.Length),
            (unit, 2L * unit - 1, content.Length),
            (2L * unit, 3L * unit - 1, content.Length),
            (3L * unit, content.Length - 1L, content.Length)
        }));
        Assert.That(_graph.Requests.Where(r => r.Request.Contains("upload.fake")).Select(r => r.Authorization),
            Is.All.Null);
        Assert.That(reports, Is.Ordered);
        Assert.That(reports[^1], Is.EqualTo(1));
        Assert.That(result.Result, Is.InstanceOf<OneDriveFileItem>());
    }

    [Test]
    public async Task LargeFile_FragmentRetriedAfterServerError()
    {
        const int unit = OneDriveClientOptions.UploadFragmentUnit;
        var content = Pattern(2 * unit + 1);
        var provider = ProviderWith(new OneDriveClientOptions { SimpleUploadLimit = 1000, UploadChunkSize = unit });
        _graph.FragmentFailures.Enqueue(HttpStatusCode.ServiceUnavailable);
        _graph.FragmentFailures.Enqueue(HttpStatusCode.BadGateway);

        var result = await provider.CopyFileInAsync(new BytesFileItem("big.bin", content), $"onedrive:{_folderId}/big.bin",
            new NullProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_graph.Find(_folderId, "big.bin")!.Content.AsSpan().SequenceEqual(content), Is.True);
        Assert.That(_delays, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task LargeFile_Cancelled_CancelsSessionAndLeavesOldFile()
    {
        const int unit = OneDriveClientOptions.UploadFragmentUnit;
        _graph.AddFile(_folderId, "big.bin", [9, 9, 9]);
        var provider = ProviderWith(new OneDriveClientOptions { SimpleUploadLimit = 1000, UploadChunkSize = unit });
        using var cts = new CancellationTokenSource();

        var result = await provider.CopyFileInAsync(new BytesFileItem("big.bin", Pattern(3 * unit)),
            $"onedrive:{_folderId}/big.bin", new SyncProgress(p => { if (p > 0) cts.Cancel(); }), cts.Token);

        Assert.That(result.Exception, Is.InstanceOf<OperationCanceledException>());
        Assert.That(_graph.UploadSessionsCancelled, Is.EqualTo(1));
        Assert.That(_graph.Find(_folderId, "big.bin")!.Content, Is.EqualTo(new byte[] { 9, 9, 9 }));
    }

    [Test]
    public async Task Upload_ReplacesTakenName_FromNonSeekableSource()
    {
        _graph.AddFile(_folderId, "a.txt", [1]);

        var result = await _provider.CopyFileInAsync(new BytesFileItem("a.txt", [5, 6, 7], seekable: false),
            $"onedrive:{_folderId}/a.txt", new NullProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_graph.Children(_folderId).Single().Content, Is.EqualTo(new byte[] { 5, 6, 7 }));
    }

    [Test]
    public async Task CreateEmptyTextFile_NeverReplaces()
    {
        _graph.AddFile(_folderId, "note.txt", [1]);

        var result = await _provider.CreateEmptyTextFileAsync("note.txt");

        Assert.That(result.Result!.Name, Is.EqualTo("note (2).txt"));
        Assert.That(_graph.Find(_folderId, "note.txt")!.Content, Is.EqualTo(new byte[] { 1 }));
        Assert.That(_graph.Requests.Single(r => r.Request.StartsWith("PUT")).Request, Does.Contain("conflictBehavior=fail"));
    }

    [Test]
    public void Options_ChunkSizeMustBeMultipleOf320KiB() =>
        Assert.Throws<ArgumentException>(() => _graph.CreateClient(new OneDriveClientOptions { UploadChunkSize = 1_000_000 }));

    [TestCase(new[] { "26-" }, 26L)]
    [TestCase(new[] { "12345-55232", "77829-99375" }, 12345L)]
    [TestCase(new string[0], null)]
    public void NextExpectedOffset_IsStartOfFirstRange(string[] ranges, long? expected) =>
        Assert.That(OneDriveClient.NextExpectedOffset(ranges), Is.EqualTo(expected));

    // ── Download ────────────────────────────────────────────────────────────

    [Test]
    public async Task Download_FollowsRedirectWithoutToken()
    {
        _graph.AddFile(_folderId, "a.bin", Pattern(5000));

        var file = await ItemAsync<IFileItem>("a.bin");
        var (stream, length) = await file.OpenReadAsync();
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(Pattern(5000)));
        }

        Assert.That(length, Is.EqualTo(5000));
        Assert.That(_graph.Requests.Single(r => r.Request.Contains("download.fake")).Authorization, Is.Null);
    }

    // ── Copy and move ───────────────────────────────────────────────────────

    [Test]
    public async Task ServerSideCopy_PollsMonitorUntilCompleted()
    {
        _graph.AddFile(_folderId, "a.txt", [1, 2, 3]);
        var target = _graph.AddFolder(FakeGraph.RootId, "Target");
        var reports = new List<double>();

        var result = await _provider.CopyFileInAsync(await ItemAsync<IFileItem>("a.txt"), $"onedrive:{target}/b.txt",
            new SyncProgress(reports.Add));

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_graph.Find(target, "b.txt")!.Content, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(result.Result!.FullName, Is.EqualTo($"onedrive:{_graph.Find(target, "b.txt")!.Id}"));

        var polls = _graph.Requests.Where(r => r.Request.Contains("monitor.fake")).ToList();
        Assert.That(polls, Has.Count.EqualTo(_graph.CopyPollsBeforeDone + 1));
        Assert.That(polls.Select(p => p.Authorization), Is.All.Null, "the monitor URL is pre-authenticated");
        Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) }));
        Assert.That(reports, Does.Contain(0.5));
        Assert.That(_graph.UploadSessionsCreated + _graph.Requests.Count(r => r.Request.StartsWith("PUT")), Is.Zero);
    }

    [Test]
    public async Task ServerSideCopy_IntoRoot_SendsRealRootIdAndDriveId()
    {
        _graph.AddFile(_folderId, "a.txt", [1]);

        var result = await _provider.CopyFileInAsync(await ItemAsync<IFileItem>("a.txt"), "onedrive:root/a.txt",
            new NullProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_graph.Find(FakeGraph.RootId, "a.txt"), Is.Not.Null);
    }

    [Test]
    public async Task ServerSideCopy_OntoTakenName_ReplacesThroughUpload()
    {
        _graph.AddFile(_folderId, "a.txt", [1]);
        var target = _graph.AddFolder(FakeGraph.RootId, "Target");
        _graph.AddFile(target, "a.txt", [7, 7]);

        var result = await _provider.CopyFileInAsync(await ItemAsync<IFileItem>("a.txt"), $"onedrive:{target}/a.txt",
            new NullProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_graph.Children(target).Single().Content, Is.EqualTo(new byte[] { 1 }));
        Assert.That(_graph.Find(_folderId, "a.txt"), Is.Not.Null);
    }

    [Test]
    public async Task ItemsOfAnotherAccount_AreNotCopiedOnTheServer()
    {
        var other = new FakeGraph();
        var otherFolder = other.AddFolder(FakeGraph.RootId, "Other");
        other.AddFile(otherFolder, "x.txt", [4, 2]);
        var otherFile = (await other.CreateClient().CreateProvider(otherFolder).GetItemsAsync(FileProviderFilter.ShowAll)
            .ToListAsync()).OfType<IFileItem>().Single();

        var result = await _provider.CopyFileInAsync(otherFile, $"onedrive:{_folderId}/x.txt", new NullProgress());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_graph.Find(_folderId, "x.txt")!.Content, Is.EqualTo(new byte[] { 4, 2 }));
        Assert.That(_graph.Requests.Any(r => r.Request.Contains("/copy")), Is.False);
        other.Dispose();
    }

    [Test]
    public async Task FolderCopy_IsOneServerCopy_AndTakenNameMeansRecurse()
    {
        var src = _graph.AddFolder(_folderId, "Src");
        _graph.AddFile(_graph.AddFolder(src, "Deep"), "d.txt", [8]);
        _graph.AddFolder(_folderId, "Taken");
        var folder = await ItemAsync<IFolderItem>("Src");

        var copied = await _provider.CopyFolderInAsync(folder, $"onedrive:{_folderId}/Copy", new NullProgress());
        var taken = await _provider.CopyFolderInAsync(folder, $"onedrive:{_folderId}/Taken", new NullProgress());

        Assert.That(copied.Result?.Name, Is.EqualTo("Copy"));
        Assert.That(_graph.Find(_graph.Find(_graph.Find(_folderId, "Copy")!.Id, "Deep")!.Id, "d.txt"), Is.Not.Null);
        Assert.That(taken.IsSuccess, Is.True);
        Assert.That(taken.Result, Is.Null);
    }

    [Test]
    public async Task Move_IsOnePatch_AndTakenNameBecomesReplace()
    {
        _graph.AddFile(_folderId, "a.txt", [1]);
        _graph.AddFile(_folderId, "b.txt", [2]);
        var target = _graph.AddFolder(FakeGraph.RootId, "Target");
        _graph.AddFile(target, "b.txt", [3]);

        var moved = await _provider.MoveFileInAsync(await ItemAsync<IFileItem>("a.txt"), $"onedrive:{target}/a2.txt",
            new NullProgress());
        var replaced = await _provider.MoveFileInAsync(await ItemAsync<IFileItem>("b.txt"), $"onedrive:{target}/b.txt",
            new NullProgress());

        Assert.That(moved.IsSuccess && replaced.IsSuccess, Is.True, moved.ErrorMessage ?? replaced.ErrorMessage);
        Assert.That(_graph.Requests.Count(r => r.Request.StartsWith("PATCH")), Is.EqualTo(2));
        Assert.That(_graph.Find(target, "a2.txt")!.Content, Is.EqualTo(new byte[] { 1 }));
        Assert.That(_graph.Find(target, "b.txt")!.Content, Is.EqualTo(new byte[] { 2 }));
        Assert.That(_graph.Children(_folderId), Is.Empty);
    }

    [Test]
    public async Task Move_ToSamePlace_IsNoOp()
    {
        _graph.AddFile(_folderId, "a.txt", [1]);

        var result = await _provider.MoveFileInAsync(await ItemAsync<IFileItem>("a.txt"), $"onedrive:{_folderId}/a.txt",
            new NullProgress());

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(_graph.Requests.Any(r => r.Request.StartsWith("PATCH")), Is.False);
    }

    [Test]
    public async Task FolderMove_IsOnePatch_AndForeignFolderMeansRecurse()
    {
        _graph.AddFolder(_folderId, "Src");
        var target = _graph.AddFolder(FakeGraph.RootId, "Target");

        var moved = await _provider.TryMoveFolderAsync(await ItemAsync<IFolderItem>("Src"), $"onedrive:{target}/Src",
            new NullProgress());
        var foreign = await _provider.TryMoveFolderAsync(new ForeignFolder(), $"onedrive:{target}/X", new NullProgress());

        Assert.That(moved.Result?.FullName, Is.EqualTo($"onedrive:{_graph.Find(target, "Src")!.Id}"));
        Assert.That(foreign.IsSuccess, Is.True);
        Assert.That(foreign.Result, Is.Null);
    }

    [Test]
    public async Task Copy_ToItemFullName_IsRefused()
    {
        _graph.AddFile(_folderId, "a.txt", [1]);

        var result = await _provider.CopyFileInAsync(new BytesFileItem("a.txt", [1]), $"onedrive:{_folderId}",
            new NullProgress());

        Assert.That(result.Exception, Is.InstanceOf<ArgumentException>());
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Delete_ToRecycleBin_IsDelete()
    {
        var id = _graph.AddFile(_folderId, "a.txt", [1]);

        var result = await _provider.DeleteAsync([await ItemAsync<IFileItem>("a.txt")], toRecycleBin: true);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(_graph.Get(id).Recycled, Is.True, "in the recycle bin, not gone");
        Assert.That(_graph.Requests.Any(r => r.Request.Contains("permanentDelete")), Is.False);
    }

    [Test]
    public async Task Delete_Permanently_WhereRefused_IsNotSupportedAndTouchesNothing()
    {
        var a = _graph.AddFile(_folderId, "a.txt", [1]);
        var b = _graph.AddFile(_folderId, "b.txt", [2]);
        _graph.PermanentDeleteSupported = false;

        var result = await _provider.DeleteAsync(
            await _provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync(), toRecycleBin: false);

        Assert.That(result.Exception, Is.InstanceOf<NotSupportedException>());
        Assert.That(_graph.Exists(a) && _graph.Exists(b), Is.True);
        Assert.That(_graph.Requests.Any(r => r.Request.StartsWith("DELETE")), Is.False, "never falls back to the bin");
    }

    [Test]
    public async Task Rename_IsPatchOfName()
    {
        var id = _graph.AddFile(_folderId, "a.txt", [1]);
        _graph.AddFile(_folderId, "taken.txt", [1]);
        var file = await ItemAsync<IFileItem>("a.txt");

        var renamed = await _provider.RenameAsync(file, "b.txt");
        var conflict = await _provider.RenameAsync(file, "taken.txt");

        Assert.That(renamed.IsSuccess, Is.True);
        Assert.That(_graph.Get(id).Name, Is.EqualTo("b.txt"));
        Assert.That((conflict.Exception as OneDriveApiException)?.Code, Is.EqualTo("nameAlreadyExists"));
    }

    // ── Throttling and tokens ───────────────────────────────────────────────

    [Test]
    public async Task Throttled_WaitsRetryAfterAndRetries()
    {
        _graph.GraphFailures.Enqueue((HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)));
        _graph.GraphFailures.Enqueue((HttpStatusCode.ServiceUnavailable, null));

        var drive = await _client.GetDriveAsync();

        Assert.That(drive.TotalSize, Is.GreaterThan(0));
        Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(2) }));
    }

    [Test]
    public void Throttled_GivesUpAfterMaxRetries()
    {
        var client = _graph.CreateClient(new OneDriveClientOptions { MaxRetries = 2, MaxRetryDelay = TimeSpan.FromSeconds(30) },
            _delays);
        for (var i = 0; i < 3; i++)
            _graph.GraphFailures.Enqueue((HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(5)));

        var e = Assert.ThrowsAsync<OneDriveApiException>(() => client.GetDriveAsync());

        Assert.That(e!.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) }));
    }

    [Test]
    public async Task Unauthorized_AsksForNewTokenOnce()
    {
        var forced = 0;
        var good = _graph.AccessToken;
        var issued = "stale";
        var client = _graph.CreateClient(token: force =>
        {
            if (force)
            {
                forced++;
                issued = good;
            }

            return issued;
        });

        var drive = await client.GetDriveAsync();
        _graph.AccessToken = "token-2";
        var e = Assert.ThrowsAsync<OneDriveApiException>(() => client.GetDriveAsync());

        Assert.That(drive, Is.Not.Null);
        Assert.That(forced, Is.EqualTo(2), "once per call that met a 401");
        Assert.That(e!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ── Search ──────────────────────────────────────────────────────────────

    [TestCase(true)]
    [TestCase(false)]
    public async Task Search_KeepsOnlyThisFolderSubtree(bool withPath)
    {
        _graph.SearchIncludesPath = withPath;
        var deep = _graph.AddFolder(_graph.AddFolder(_folderId, "Plans"), "2026");
        _graph.AddFile(deep, "Budget plan.xlsx", [1]);
        _graph.AddFile(_folderId, "plan.md", [1]);
        _graph.AddFile(_graph.AddFolder(FakeGraph.RootId, "Elsewhere"), "plan.txt", [1]);
        _graph.AddFile(FakeGraph.RootId, "plan-root.txt", [1]);

        var found = await _provider.SearchAsync("plan").ToListAsync();
        var everywhere = await _client.CreateProvider().SearchAsync("plan").ToListAsync();

        Assert.That(found.Select(i => i.Name), Is.EquivalentTo(new[] { "Plans", "Budget plan.xlsx", "plan.md" }));
        Assert.That(everywhere, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task Search_EscapesQuotes()
    {
        _graph.AddFile(_folderId, "Anna's notes.txt", [1]);

        var found = await _provider.SearchAsync("anna's").ToListAsync();

        Assert.That(found.Single().Name, Is.EqualTo("Anna's notes.txt"));
    }

    // ── Thumbnails ──────────────────────────────────────────────────────────

    [Test]
    public async Task Thumbnail_CustomSizeThroughRedirect()
    {
        _graph.AddFile(_folderId, "photo.jpg", [1]);
        _graph.AddFile(_folderId, "readme.txt", [1]);

        var photo = await ItemAsync<IThumbnailItem>("photo.jpg");
        var text = await ItemAsync<IThumbnailItem>("readme.txt");
        using var reader = new StreamReader((await photo.OpenThumbnailAsync(256))!);

        Assert.That(photo.HasThumbnail, Is.True);
        Assert.That(text.HasThumbnail, Is.False);
        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("thumbnail c256x256"));
        Assert.That(await text.OpenThumbnailAsync(256), Is.Null);
        Assert.That(_graph.Requests.Single(r => r.Request.Contains("thumbs.fake")).Authorization, Is.Null);
    }

    [TestCase(64, "small")]
    [TestCase(150, "medium")]
    [TestCase(512, "large")]
    public async Task Thumbnail_FallsBackToStandardSize(int size, string expected)
    {
        _graph.RefuseCustomThumbnailSizes = true;
        _graph.AddFile(_folderId, "photo.png", [1]);

        using var reader = new StreamReader((await (await ItemAsync<IThumbnailItem>("photo.png")).OpenThumbnailAsync(size))!);

        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo($"thumbnail {expected}"));
    }

    // ── Public links ────────────────────────────────────────────────────────

    [Test]
    public async Task PublicLink_CreatedOnce_ThenRemoved()
    {
        var id = _graph.AddFile(_folderId, "a.txt", [1]);
        var file = await ItemAsync<IPublicLinkItem>("a.txt");

        var first = await file.GetPublicLinkAsync();
        var second = await file.GetPublicLinkAsync();
        var removed = await file.RemovePublicLinkAsync();
        var removedAgain = await file.RemovePublicLinkAsync();

        Assert.That(first.Result, Is.EqualTo(second.Result));
        Assert.That(first.Result!.Host, Is.EqualTo("1drv.ms"));
        Assert.That(_graph.Requests.Count(r => r.Request.Contains("/createLink")), Is.EqualTo(1));
        Assert.That(removed.IsSuccess && removedAgain.IsSuccess, Is.True);
        Assert.That(_graph.Get(id).Links, Is.Empty);
    }

    [Test]
    public async Task PublicLink_OfFolder()
    {
        _graph.AddFolder(_folderId, "Shared");

        var link = await (await ItemAsync<IFolderItem>("Shared") as IPublicLinkItem)!.GetPublicLinkAsync();

        Assert.That(link.IsSuccess, Is.True, link.ErrorMessage);
    }

    [TestCase("https://graph.microsoft.com/v1.0/drives/b!x/items/01ABC?x=1", "01ABC")]
    [TestCase("https://contoso.sharepoint.com/_api/v2.0/drives/b!x/items/ITEM%21101", "ITEM!101")]
    [TestCase("https://api.onedrive.com/monitor/123", null)]
    public void ItemIdFromUrl_ReadsIdAfterItems(string url, string? expected) =>
        Assert.That(OneDriveClient.ItemIdFromUrl(new Uri(url)), Is.EqualTo(expected));

    // ── Doubles ─────────────────────────────────────────────────────────────

    private sealed class BytesFileItem(string name, byte[] content, bool seekable = true) : IFileItem
    {
        public string Name => name;
        public string FullName => $"/elsewhere/{name}";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        public long Size => content.Length;
        long? IFileProviderItem.Size => content.Length;
        public string Extension => Path.GetExtension(name);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
            Task.FromResult<(Stream, long)>((seekable ? new MemoryStream(content) : new ForwardOnlyStream(content),
                content.Length));

        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ForwardOnlyStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    private sealed class ForeignFolder : IFolderItem
    {
        public string Name => "X";
        public string FullName => "/elsewhere/X";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        public long? Size => null;
        public IFileProvider CreateProvider() => throw new NotSupportedException();
    }

    private sealed class NullProgress : IProgress<double>
    {
        public void Report(double value) { }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
