using System.Net;
using System.Reflection;
using Egorozh.YandexDisk.Client;
using Egorozh.YandexDisk.Client.Clients;
using Egorozh.YandexDisk.Client.Protocol;
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

    private static Resource FileResource(string path) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        Path = path,
        Type = ResourceType.File
    };

    // YandexApiException has only an internal constructor.
    private static YandexApiException ApiError(HttpStatusCode status)
        => (YandexApiException)Activator.CreateInstance(typeof(YandexApiException),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [status, status.ToString(), null], null)!;

    private sealed class FakeDiskApi : IDiskApi, IMetaInfoClient, ICommandsClient, IFilesClient
    {
        public Func<ResourceRequest, Resource> GetInfo { get; init; } = _ => throw ApiError(HttpStatusCode.NotFound);
        public byte[] Content { get; init; } = [];

        public List<(string From, string To)> Copies { get; } = [];
        public List<(string From, string To)> Moves { get; } = [];
        public List<string> Deletes { get; } = [];
        public List<string> Downloads { get; } = [];
        public List<(string Path, byte[] Data)> Uploads { get; } = [];

        public IFilesClient Files => this;
        public IMetaInfoClient MetaInfo => this;
        public ICommandsClient Commands => this;

        public void Dispose() { }

        public Task<Resource> GetInfoAsync(ResourceRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(GetInfo(request));

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

        public Task<Link> DeleteAsync(DeleteFileRequest request, CancellationToken cancellationToken = default)
        {
            Deletes.Add(request.Path);
            return Done();
        }

        public Task<Operation> GetOperationStatus(Link link, CancellationToken cancellationToken = default)
            => Task.FromResult(new Operation { Status = OperationStatus.Success });

        public Task<Link> GetUploadLinkAsync(string path, bool overwrite, CancellationToken cancellationToken = default)
            => Task.FromResult(new Link { Href = path });

        public Task<Link> GetDownloadLinkAsync(string path, CancellationToken cancellationToken = default)
        {
            Downloads.Add(path);
            return Task.FromResult(new Link { Href = path });
        }

        public Task<(Stream, long)> DownloadFastAsync(Link link, CancellationToken cancellationToken = default)
            => Task.FromResult<(Stream, long)>((new MemoryStream(Content), Content.Length));

        public async Task UploadWithProgressAsync(Link link, Stream file, long fileSize, Action<double> progressCallback,
            CancellationToken ct = default)
        {
            using var copy = new MemoryStream();
            await file.CopyToAsync(copy, ct);
            Uploads.Add((link.Href, copy.ToArray()));
        }

        public Task UploadAsync(Link link, Stream file, CancellationToken cancellationToken = default)
            => UploadWithProgressAsync(link, file, file.Length, _ => { }, cancellationToken);

        private static Task<Link> Done() => Task.FromResult(new Link { HttpStatusCode = HttpStatusCode.Created });

        public Task<Disk> GetDiskInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Resource> GetTrashInfoAsync(ResourceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FilesResourceList> GetFilesInfoAsync(FilesResourceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LastUploadedResourceList> GetLastUploadedInfoAsync(LastUploadedResourceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Resource> AppendCustomProperties(string path, IDictionary<string, string> properties, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Link> PublishFolderAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Link> UnpublishFolderAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Link> CreateDictionaryAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Link> EmptyTrashAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
