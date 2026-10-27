using System.Net;
using XFiler.YandexDisk.Client;
using XFiler.YandexDisk.Client.Clients;
using XFiler.YandexDisk.Client.Protocol;
using FileVault.Core;

namespace FileVault.YandexDisk.Tests;

/// <summary>
/// Unit tests against a fake IDiskApi — no Yandex account needed.
/// </summary>
[TestFixture]
public class YandexDiskFileProviderTests
{
    [Test]
    public async Task ContainsFileAsync_NotFound_ReturnsFalse()
    {
        var api = new FakeDiskApi { GetInfo = _ => throw ApiError(HttpStatusCode.NotFound) };
        var provider = new YandexDiskFileProvider(api, "disk:/Docs");

        Assert.That(await provider.ContainsFileAsync("report.pdf"), Is.False);
    }

    [Test]
    public async Task ContainsFileAsync_ExistingFile_ReturnsTrue()
    {
        var api = new FakeDiskApi { GetInfo = r => new Resource { Path = r.Path, Type = ResourceType.File } };
        var provider = new YandexDiskFileProvider(api, "disk:/Docs");

        Assert.That(await provider.ContainsFileAsync("report.pdf"), Is.True);
        Assert.That(await provider.ContainsFolderAsync("report.pdf"), Is.False);
    }

    [Test]
    public void ContainsFileAsync_NetworkError_Throws()
    {
        var api = new FakeDiskApi { GetInfo = _ => throw new HttpRequestException("Network is unreachable") };
        var provider = new YandexDiskFileProvider(api, "disk:/Docs");

        Assert.That(async () => await provider.ContainsFileAsync("report.pdf"), Throws.TypeOf<HttpRequestException>());
    }

    [Test]
    public void ContainsFolderAsync_ServerError_Throws()
    {
        var api = new FakeDiskApi { GetInfo = _ => throw ApiError(HttpStatusCode.InternalServerError) };
        var provider = new YandexDiskFileProvider(api, "disk:/Docs");

        Assert.That(async () => await provider.ContainsFolderAsync("Photos"), Throws.InstanceOf<YandexApiException>());
    }

    [Test]
    public void ResolveDestinationFileAsync_NetworkError_DoesNotPickExistingName()
    {
        // Answering "name is free" here would make the following upload overwrite the existing file.
        var api = new FakeDiskApi { GetInfo = _ => throw new HttpRequestException("timeout") };
        var provider = new YandexDiskFileProvider(api, "disk:/Docs");

        Assert.That(async () => await provider.ResolveDestinationFileAsync(new FakeFileItem("report.pdf"), overwrite: false),
            Throws.TypeOf<HttpRequestException>());
    }

    [Test]
    public async Task CopyFileInAsync_SameAccount_UsesServerCopy()
    {
        var api = new FakeDiskApi();
        var provider = new YandexDiskFileProvider(api, "disk:/Dest");
        var source = new YandexDiskFileItem(FileResource("disk:/Src/a.txt"), api);

        var result = await provider.CopyFileInAsync(source, "disk:/Dest/a.txt", new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(api.Copies, Is.EqualTo(new[] { ("disk:/Src/a.txt", "disk:/Dest/a.txt") }));
        Assert.That(api.Uploads, Is.Empty);
    }

    [Test]
    public async Task CopyFileInAsync_OtherAccount_DownloadsAndUploads()
    {
        var sourceApi = new FakeDiskApi { Content = [1, 2, 3] };
        var targetApi = new FakeDiskApi();
        var provider = new YandexDiskFileProvider(targetApi, "disk:/Dest");
        var source = new YandexDiskFileItem(FileResource("disk:/Src/a.txt"), sourceApi);

        var result = await provider.CopyFileInAsync(source, "disk:/Dest/a.txt", new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(targetApi.Copies, Is.Empty, "server-side copy would read disk:/Src/a.txt of the wrong account");
        Assert.That(sourceApi.Downloads, Is.EqualTo(new[] { "disk:/Src/a.txt" }));
        Assert.That(targetApi.Uploads, Is.EqualTo(new[] { ("disk:/Dest/a.txt", new byte[] { 1, 2, 3 }) }));
    }

    [Test]
    public async Task MoveFileInAsync_OtherAccount_DoesNotMoveOnTargetAccount()
    {
        var sourceApi = new FakeDiskApi { Content = [4] };
        var targetApi = new FakeDiskApi();
        var provider = new YandexDiskFileProvider(targetApi, "disk:/Dest");
        var source = new YandexDiskFileItem(FileResource("disk:/Src/a.txt"), sourceApi);

        var result = await provider.MoveFileInAsync(source, "disk:/Dest/a.txt", new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(targetApi.Moves, Is.Empty);
        Assert.That(targetApi.Uploads, Has.Count.EqualTo(1));
        Assert.That(sourceApi.Deletes, Is.EqualTo(new[] { "disk:/Src/a.txt" }));
    }

    [Test]
    public async Task FolderOperations_OtherAccount_LetOrchestratorRecurse()
    {
        var sourceApi = new FakeDiskApi();
        var targetApi = new FakeDiskApi();
        var provider = new YandexDiskFileProvider(targetApi, "disk:/Dest");
        var folder = new YandexDiskFolderItem(
            new Resource { Name = "Photos", Path = "disk:/Photos", Type = ResourceType.Dir }, sourceApi);

        var copy = await provider.CopyFolderInAsync(folder, "disk:/Dest/Photos", new Progress<double>());
        var move = await provider.TryMoveFolderAsync(folder, "disk:/Dest/Photos", new Progress<double>());

        Assert.That(copy.IsSuccess && copy.Result is null, Is.True);
        Assert.That(move.IsSuccess && move.Result is null, Is.True);
        Assert.That(targetApi.Copies, Is.Empty);
        Assert.That(targetApi.Moves, Is.Empty);
    }

    [Test]
    public async Task CopyFileInAsync_AsyncOperationFails_ReturnsFailure()
    {
        var api = new FakeDiskApi { AsyncOperationStatus = OperationStatus.Failure };
        var provider = new YandexDiskFileProvider(api, "disk:/Dest");
        var source = new YandexDiskFileItem(FileResource("disk:/Src/a.txt"), api);

        var result = await provider.CopyFileInAsync(source, "disk:/Dest/a.txt", new Progress<double>());

        Assert.That(result.IsSuccess, Is.False, "a failed copy operation must not be reported as done");
        Assert.That(result.Exception, Is.InstanceOf<YandexOperationFailedException>());
    }

    [Test]
    public async Task DeleteAsync_CancelledWhileOperationRuns_ReturnsCancelledFailure()
    {
        var api = new FakeDiskApi { AsyncOperationStatus = OperationStatus.InProgress };
        var provider = new YandexDiskFileProvider(api, "disk:/Docs");
        var file = new YandexDiskFileItem(FileResource("disk:/Docs/a.txt"), api);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await provider.DeleteAsync([file], toRecycleBin: true, cts.Token);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.InstanceOf<OperationCanceledException>());
    }

    private const string Preview =
        "https://downloader.disk.yandex.ru/preview/abc/inf/def?uid=1&filename=a.jpg&size=S&crop=0";

    [Test]
    public async Task OpenThumbnailAsync_AsksPreviewOfNearestLargerSize()
    {
        var api = new FakeDiskApi { Content = [7, 8] };
        var resource = FileResource("disk:/Photos/a.jpg");
        resource.Preview = Preview;
        var file = new YandexDiskFileItem(resource, api);

        await using var thumbnail = await file.OpenThumbnailAsync(256);

        Assert.That(file.HasThumbnail, Is.True);
        Assert.That(api.DownloadedLinks, Is.EqualTo(new[] { Preview.Replace("size=S", "size=M") }));
        Assert.That(((MemoryStream) thumbnail!).ToArray(), Is.EqualTo(new byte[] { 7, 8 }));
    }

    [Test]
    public async Task OpenThumbnailAsync_SizeRefused_UsesListedPreview()
    {
        var api = new FakeDiskApi
        {
            DownloadLink = link => link == Preview ? [5] : throw ApiError(HttpStatusCode.BadRequest)
        };
        var resource = FileResource("disk:/Photos/a.jpg");
        resource.Preview = Preview;

        await using var thumbnail = await new YandexDiskFileItem(resource, api).OpenThumbnailAsync(256);

        Assert.That(((MemoryStream) thumbnail!).ToArray(), Is.EqualTo(new byte[] { 5 }));
        Assert.That(api.DownloadedLinks, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task OpenThumbnailAsync_NoPreview_ReturnsNull()
    {
        var api = new FakeDiskApi();
        var file = new YandexDiskFileItem(FileResource("disk:/Docs/a.zip"), api);

        Assert.That(file.HasThumbnail, Is.False);
        Assert.That(await file.OpenThumbnailAsync(256), Is.Null);
        Assert.That(api.DownloadedLinks, Is.Empty);
    }

    [Test]
    public async Task SearchAsync_KeepsNameMatchesBelowFolder()
    {
        var api = new FakeDiskApi
        {
            AllFiles =
            [
                FileResource("disk:/Docs/Report.pdf"),
                FileResource("disk:/Docs/notes.txt"),
                FileResource("disk:/Docs/sub/annual report.txt"),
                FileResource("disk:/Docs/Reports/a.bin"),
                FileResource("disk:/Docs/Reports/b.bin"),
                FileResource("disk:/Other/report.doc"),
                FileResource("disk:/DocsOld/report.doc")
            ],
            GetInfo = r => FolderResource(r.Path)
        };
        var provider = new YandexDiskFileProvider(api, "disk:/Docs");

        var found = await SearchAsync(provider, "REPORT");

        Assert.That(found.Select(i => i.FullName), Is.EquivalentTo(new[]
        {
            "disk:/Docs/Report.pdf", "disk:/Docs/sub/annual report.txt", "disk:/Docs/Reports"
        }));
        Assert.That(found.Single(i => i.FullName == "disk:/Docs/Reports"), Is.InstanceOf<YandexDiskFolderItem>());
        Assert.That(found.Where(i => i.FullName != "disk:/Docs/Reports"), Is.All.InstanceOf<YandexDiskFileItem>());
        Assert.That(api.InfoRequests, Is.EqualTo(new[] { "disk:/Docs/Reports" }), "a folder is read once");
    }

    [Test]
    public async Task SearchAsync_Root_FindsFoldersAtEveryDepth()
    {
        var api = new FakeDiskApi
        {
            AllFiles = [FileResource("disk:/Фото/Отпуск/фото.jpg"), FileResource("disk:/a.txt")],
            GetInfo = r => FolderResource(r.Path)
        };

        var found = await SearchAsync(new YandexDiskFileProvider(api, "disk:/"), "ФОТО");

        Assert.That(found.Select(i => i.FullName), Is.EquivalentTo(new[] { "disk:/Фото", "disk:/Фото/Отпуск/фото.jpg" }));
    }

    [Test]
    public async Task SearchAsync_PathWithoutScheme_StillMatches()
    {
        var api = new FakeDiskApi { AllFiles = [FileResource("disk:/Docs/a.txt")] };

        var found = await SearchAsync(new YandexDiskFileProvider(api, "/Docs/"), "a");

        Assert.That(found.Select(i => i.FullName), Is.EqualTo(new[] { "disk:/Docs/a.txt" }));
    }

    [Test]
    public async Task SearchAsync_PagesThroughTheWholeFileList()
    {
        var api = new FakeDiskApi
        {
            AllFiles = Enumerable.Range(0, 2500).Select(i => FileResource($"disk:/Docs/file{i}.txt")).ToList()
        };

        var found = await SearchAsync(new YandexDiskFileProvider(api, "disk:/Docs"), "file24");

        Assert.That(found, Has.Count.EqualTo(111)); // file24, file240..249, file2400..2499
        Assert.That(api.FileListOffsets, Is.EqualTo(new[] { 0, 1000, 2000 }));
    }

    [Test]
    public async Task SearchAsync_ServerCapsThePage_KeepsPaging()
    {
        var api = new FakeDiskApi
        {
            MaxFilesPerPage = 100,
            AllFiles = Enumerable.Range(0, 150).Select(i => FileResource($"disk:/f{i}.txt")).ToList()
        };

        var found = await SearchAsync(new YandexDiskFileProvider(api, "disk:/"), "f");

        Assert.That(found, Has.Count.EqualTo(150));
        Assert.That(api.FileListOffsets, Is.EqualTo(new[] { 0, 100 }));
    }

    [Test]
    public async Task SearchAsync_FolderGoneMeanwhile_IsSkipped()
    {
        var api = new FakeDiskApi { AllFiles = [FileResource("disk:/Report/a.txt")] };

        var found = await SearchAsync(new YandexDiskFileProvider(api, "disk:/"), "report");

        Assert.That(found, Is.Empty);
    }

    [Test]
    public async Task SearchAsync_EmptyQuery_FindsNothingWithoutRequest()
    {
        var api = new FakeDiskApi { AllFiles = [FileResource("disk:/a.txt")] };

        Assert.That(await SearchAsync(new YandexDiskFileProvider(api, "disk:/"), ""), Is.Empty);
        Assert.That(api.FileListOffsets, Is.Empty);
    }

    [Test]
    public async Task GetPublicLinkAsync_NotPublished_PublishesAndReturnsLink()
    {
        var api = PublishingApi();
        var file = new YandexDiskFileItem(FileResource("disk:/Docs/a.txt"), api);

        var result = await file.GetPublicLinkAsync();

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result, Is.EqualTo(new Uri("https://yadi.sk/d/1")));
        Assert.That(api.Publishes, Is.EqualTo(new[] { "disk:/Docs/a.txt" }));
    }

    [Test]
    public async Task GetPublicLinkAsync_AlreadyPublished_ReturnsExistingLink()
    {
        var api = PublishingApi();
        api.Published["disk:/Photos"] = "https://yadi.sk/d/existing";
        var folder = new YandexDiskFolderItem(FolderResource("disk:/Photos"), api);

        var result = await folder.GetPublicLinkAsync();

        Assert.That(result.Result, Is.EqualTo(new Uri("https://yadi.sk/d/existing")));
        Assert.That(api.Publishes, Is.Empty);
    }

    [Test]
    public async Task GetPublicLinkAsync_ApiError_ReturnsFailure()
    {
        var api = new FakeDiskApi { GetInfo = _ => throw ApiError(HttpStatusCode.Forbidden) };
        var file = new YandexDiskFileItem(FileResource("disk:/Docs/a.txt"), api);

        var result = await file.GetPublicLinkAsync();

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.InstanceOf<YandexApiException>());
    }

    [Test]
    public async Task RemovePublicLinkAsync_Published_Unpublishes()
    {
        var api = PublishingApi();
        api.Published["disk:/Photos"] = "https://yadi.sk/d/existing";
        var folder = new YandexDiskFolderItem(FolderResource("disk:/Photos"), api);

        var result = await folder.RemovePublicLinkAsync();

        Assert.That(result.IsSuccess && result.Result, Is.True, result.ErrorMessage);
        Assert.That(api.Unpublishes, Is.EqualTo(new[] { "disk:/Photos" }));
        Assert.That(api.Published, Is.Empty);
    }

    [Test]
    public async Task RemovePublicLinkAsync_NoLink_SucceedsWithoutRequest()
    {
        var api = PublishingApi();
        var file = new YandexDiskFileItem(FileResource("disk:/Docs/a.txt"), api);

        var result = await file.RemovePublicLinkAsync();

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result, Is.False);
        Assert.That(api.Unpublishes, Is.Empty);
    }

    private static FakeDiskApi PublishingApi()
    {
        var published = new Dictionary<string, string>();
        return new FakeDiskApi
        {
            Published = published,
            GetInfo = r => new Resource { Path = r.Path, PublicUrl = published.GetValueOrDefault(r.Path) }
        };
    }

    private static async Task<List<IFileProviderItem>> SearchAsync(ISearchableFileProvider provider, string query)
    {
        var found = new List<IFileProviderItem>();
        await foreach (var item in provider.SearchAsync(query))
            found.Add(item);
        return found;
    }

    private static Resource FolderResource(string path) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        Path = path,
        Type = ResourceType.Dir
    };

    private static Resource FileResource(string path) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        Path = path,
        Type = ResourceType.File
    };

    private static YandexApiException ApiError(HttpStatusCode status) => new(status, status.ToString(), error: null);

    private sealed class FakeDiskApi : IDiskApi, IMetaInfoClient, ICommandsClient, IFilesClient
    {
        public Func<ResourceRequest, Resource> GetInfo { get; init; } = _ => throw ApiError(HttpStatusCode.NotFound);
        public byte[] Content { get; init; } = [];

        // Set: copy, move and delete answer 202 Accepted and the operation reports this status.
        public OperationStatus? AsyncOperationStatus { get; init; }

        public List<(string From, string To)> Copies { get; } = [];
        public List<(string From, string To)> Moves { get; } = [];
        public List<string> Deletes { get; } = [];
        public List<string> Downloads { get; } = [];
        public List<(string Path, byte[] Data)> Uploads { get; } = [];

        public IFilesClient Files => this;
        public IMetaInfoClient MetaInfo => this;
        public ICommandsClient Commands => this;

        public void Dispose() { }

        public List<string> InfoRequests { get; } = [];

        public Task<Resource> GetInfoAsync(ResourceRequest request, CancellationToken cancellationToken = default)
        {
            InfoRequests.Add(request.Path);
            return Task.FromResult(GetInfo(request));
        }

        public List<Resource> AllFiles { get; init; } = [];
        public int? MaxFilesPerPage { get; init; }
        public List<int> FileListOffsets { get; } = [];

        public Task<FilesResourceList> GetFilesInfoAsync(FilesResourceRequest request, CancellationToken cancellationToken = default)
        {
            var offset = request.Offset ?? 0;
            var limit = Math.Min(request.Limit ?? 20, MaxFilesPerPage ?? int.MaxValue);
            FileListOffsets.Add(offset);
            return Task.FromResult(new FilesResourceList
            {
                Items = AllFiles.Skip(offset).Take(limit).ToList(),
                Limit = limit,
                Offset = offset
            });
        }

        public Dictionary<string, string> Published { get; init; } = [];
        public List<string> Publishes { get; } = [];
        public List<string> Unpublishes { get; } = [];

        public Task<Link> PublishAsync(string path, CancellationToken cancellationToken = default)
        {
            Publishes.Add(path);
            Published[path] = $"https://yadi.sk/d/{Publishes.Count}";
            return Task.FromResult(new Link { Href = path });
        }

        public Task<Link> UnpublishAsync(string path, CancellationToken cancellationToken = default)
        {
            Unpublishes.Add(path);
            Published.Remove(path);
            return Task.FromResult(new Link { Href = path });
        }

        public Task<Link> CopyAsync(CopyFileRequest request, CancellationToken cancellationToken = default)
        {
            Copies.Add((request.From, request.Path));
            return Done();
        }

        public Task<Link> MoveAsync(MoveFileRequest request, CancellationToken cancellationToken = default)
        {
            Moves.Add((request.From, request.Path));
            return Done();
        }

        public async Task<Link?> DeleteAsync(DeleteFileRequest request, CancellationToken cancellationToken = default)
        {
            Deletes.Add(request.Path);
            return await Done();
        }

        public Task<Operation> GetOperationStatusAsync(Link link, CancellationToken cancellationToken = default)
            => Task.FromResult(new Operation { Status = AsyncOperationStatus ?? OperationStatus.Success });

        public Task<Link> GetUploadLinkAsync(string path, bool overwrite, CancellationToken cancellationToken = default)
            => Task.FromResult(new Link { Href = path });

        public Task<Link> GetDownloadLinkAsync(string path, CancellationToken cancellationToken = default)
        {
            Downloads.Add(path);
            return Task.FromResult(new Link { Href = path });
        }

        public Func<string, byte[]>? DownloadLink { get; init; }
        public List<string> DownloadedLinks { get; } = [];

        public Task<(Stream Stream, long Length)> DownloadFastAsync(Link link, CancellationToken cancellationToken = default)
        {
            DownloadedLinks.Add(link.Href);
            var content = DownloadLink?.Invoke(link.Href) ?? Content;
            return Task.FromResult<(Stream, long)>((new MemoryStream(content), content.Length));
        }

        public async Task UploadWithProgressAsync(Link link, Stream file, long fileSize, Action<double>? progressCallback,
            CancellationToken ct = default)
        {
            using var copy = new MemoryStream();
            await file.CopyToAsync(copy, ct);
            Uploads.Add((link.Href, copy.ToArray()));
        }

        public Task UploadAsync(Link link, Stream file, CancellationToken cancellationToken = default)
            => UploadWithProgressAsync(link, file, file.Length, _ => { }, cancellationToken);

        private Task<Link> Done() => Task.FromResult(AsyncOperationStatus is null
            ? new Link { HttpStatusCode = HttpStatusCode.Created }
            : new Link { Href = "https://cloud-api.yandex.net/v1/disk/operations/1", HttpStatusCode = HttpStatusCode.Accepted });

        public Task<Disk> GetDiskInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Resource> GetTrashInfoAsync(ResourceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LastUploadedResourceList> GetLastUploadedInfoAsync(LastUploadedResourceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Resource> AppendCustomPropertiesAsync(string path, IDictionary<string, string?> properties, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Link> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Link?> EmptyTrashAsync(string? path = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Link> RestoreFromTrashAsync(RestoreFromTrashRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(Link link, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeFileItem(string name) : IFileItem
    {
        public string Name => name;
        public string FullName => $"fake:{name}";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        long? IFileProviderItem.Size => 0;
        public long Size => 0;
        public string Extension => Path.GetExtension(name);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
            => Task.FromResult<(Stream, long)>((new MemoryStream(), 0));

        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
