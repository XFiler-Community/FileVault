using FileVault.Core;

namespace FileVault.S3.Tests;

/// <summary>S3 behaviour beyond the shared contract, against the in-memory S3.</summary>
[TestFixture]
public class S3FileProviderTests
{
    private const string Bucket = "bucket";
    private const long MiB = 1024 * 1024;

    private FakeS3 _s3 = null!;

    [SetUp]
    public void SetUp() => _s3 = new FakeS3();

    [TearDown]
    public void TearDown() => _s3.Dispose();

    [Test]
    public async Task FullName_IsHostBucketAndKey()
    {
        var connection = _s3.Connect(Bucket);
        var provider = new S3FileProvider(connection, "/photos/2024/");

        Assert.That(new S3DriveItem(connection).FullName, Is.EqualTo("s3://s3.fake.test/bucket/"));
        Assert.That(provider.FullName, Is.EqualTo("s3://s3.fake.test/bucket/photos/2024"));
        Assert.That(provider.Name, Is.EqualTo("2024"));
        Assert.That(new S3FileProvider(connection).Name, Is.EqualTo(Bucket));

        _s3.Put(Bucket, "photos/2024/a.jpg", [1]);
        var item = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).Single();
        Assert.That(item.FullName, Is.EqualTo("s3://s3.fake.test/bucket/photos/2024/a.jpg"));
    }

    [Test]
    public async Task DriveItem_IsTheBucketRoot()
    {
        var connection = _s3.Connect(Bucket);
        _s3.Put(Bucket, "top.txt", [1]);
        var drive = new S3DriveItem(connection);

        Assert.That(drive.Name, Is.EqualTo(Bucket));
        Assert.That(drive.TotalSize, Is.Zero);
        Assert.That(drive.TotalFreeSpace, Is.Zero);
        var items = await drive.CreateProvider().GetItemsAsync(FileProviderFilter.Default).ToListAsync();
        Assert.That(items.Single().Name, Is.EqualTo("top.txt"));
    }

    [Test]
    public async Task GetItems_PagesFoldersFirst_HidesOwnMarkerAndDotItems()
    {
        _s3.PageSize = 2;
        var provider = new S3FileProvider(_s3.Connect(Bucket), "root");
        _s3.Put(Bucket, "root/", []);
        _s3.Put(Bucket, "root/a.txt", [1]);
        _s3.Put(Bucket, "root/b/", []);
        _s3.Put(Bucket, "root/c/deep/file.bin", [1]);
        _s3.Put(Bucket, "root/d.txt", [1, 2]);
        _s3.Put(Bucket, "root/e/", []);
        _s3.Put(Bucket, "root/.hidden", [1]);
        _s3.Put(Bucket, "rootless.txt", [1]);

        var items = await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync();

        Assert.That(items.Select(i => i.Name), Is.EqualTo(new[] { "b", "c", "e", "a.txt", "d.txt" }));
        Assert.That(items.Take(3), Is.All.InstanceOf<S3FolderItem>());
        Assert.That(items.OfType<S3FileItem>().Single(f => f.Name == "d.txt").Size, Is.EqualTo(2));

        var all = await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        Assert.That(all.Select(i => i.Name), Does.Contain(".hidden"));

        var deep = ((IFolderItem)items.Single(i => i.Name == "c")).CreateProvider();
        Assert.That((await deep.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).Single().Name, Is.EqualTo("deep"));
    }

    [TestCase("dir a/f+1%.txt")]
    [TestCase("Папка/Файл с пробелом.txt")]
    [TestCase("q?&=#'()!*/x~y.bin")]
    public async Task Keys_WithSpecialCharacters_RoundTrip(string key)
    {
        var connection = _s3.Connect(Bucket);
        var folderPath = key[..key.LastIndexOf('/')];
        var name = key[(key.LastIndexOf('/') + 1)..];
        var root = new S3FileProvider(connection);

        var folder = await root.CreateFolderAsync(folderPath);
        Assert.That(folder.IsSuccess, Is.True, folder.ErrorMessage);
        var provider = folder.Result!.CreateProvider();

        var copy = await provider.CopyFileInAsync(new MemoryFileItem("src", [1, 2, 3]),
            connection.FullName(key), new Progress<double>());
        Assert.That(copy.IsSuccess, Is.True, copy.ErrorMessage);

        Assert.That(_s3.Get(Bucket, key), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(await provider.ContainsFileAsync(name), Is.True);
        Assert.That(await root.ContainsFolderAsync(folderPath), Is.True);

        var listed = (await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).OfType<IFileItem>().Single();
        Assert.That(listed.Name, Is.EqualTo(name));
        var (stream, length) = await listed.OpenReadAsync();
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.That(buffer.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(length, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task VirtualHostedStyle_PutsTheBucketInTheHost()
    {
        var connection = _s3.Connect(Bucket, pathStyle: false);
        var provider = new S3FileProvider(connection);

        var result = await provider.CreateEmptyTextFileAsync("note one.txt");

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_s3.Get(Bucket, "note one.txt"), Is.Empty);
        Assert.That(await provider.ContainsFileAsync("note one.txt"), Is.True);
    }

    [Test]
    public async Task CreateFolder_WritesAMarker()
    {
        var provider = new S3FileProvider(_s3.Connect(Bucket), "docs");

        var result = await provider.CreateFolderAsync("New");

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result!.FullName, Is.EqualTo("s3://s3.fake.test/bucket/docs/New"));
        Assert.That(_s3.Get(Bucket, "docs/New/"), Is.Empty);
        Assert.That(await result.Result.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync(), Is.Empty);
    }

    [Test]
    public async Task ResolveNames_CountFilesAndFoldersAlike()
    {
        var provider = new S3FileProvider(_s3.Connect(Bucket));
        _s3.Put(Bucket, "report.pdf/", []);
        _s3.Put(Bucket, "report (2).pdf", []);

        var file = await provider.ResolveDestinationFileAsync(new MemoryFileItem("report.pdf", []), overwrite: false);
        var folder = await provider.CreateFolderAsync("report (2).pdf");

        Assert.That(file.Name, Is.EqualTo("report (3).pdf"));
        Assert.That(file.FullName, Is.EqualTo("s3://s3.fake.test/bucket/report (3).pdf"));
        Assert.That(folder.Result!.Name, Is.EqualTo("report (2).pdf (2)"));
    }

    [Test]
    public async Task Upload_KnownLengthOverThreshold_GoesInParts()
    {
        var connection = _s3.Connect(Bucket, multipartThreshold: 6 * MiB, partSize: 5 * MiB);
        var data = RandomBytes(12 * MiB + 7);
        var reports = new List<double>();

        var result = await new S3FileProvider(connection).CopyFileInAsync(new MemoryFileItem("big.bin", data),
            connection.FullName("big.bin"), new SyncProgress(reports));

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        AssertContent("big.bin", data);
        Assert.That(_s3.Requests.Count(r => r.Contains("partNumber=")), Is.EqualTo(3));
        Assert.That(result.Result!.Size, Is.EqualTo(data.Length));
        Assert.That(reports, Is.Ordered);
        Assert.That(reports[^1], Is.EqualTo(1));
    }

    [Test]
    public async Task Upload_UnknownLength_GoesInParts()
    {
        var connection = _s3.Connect(Bucket, multipartThreshold: 6 * MiB, partSize: 5 * MiB);
        var data = RandomBytes(10 * MiB);

        var result = await new S3FileProvider(connection).CopyFileInAsync(
            new MemoryFileItem("stream.bin", data, reportLength: false), connection.FullName("stream.bin"),
            new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        AssertContent("stream.bin", data);
        Assert.That(_s3.Requests.Count(r => r.Contains("partNumber=")), Is.EqualTo(2));
    }

    [Test]
    public async Task Upload_UnknownLengthSmall_IsOnePut()
    {
        var connection = _s3.Connect(Bucket, multipartThreshold: 6 * MiB, partSize: 5 * MiB);
        var data = RandomBytes(1000);

        var result = await new S3FileProvider(connection).CopyFileInAsync(
            new MemoryFileItem("small.bin", data, reportLength: false), connection.FullName("small.bin"),
            new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        AssertContent("small.bin", data);
        Assert.That(_s3.Requests.Any(r => r.Contains("uploads")), Is.False);
    }

    [Test]
    public async Task Upload_FailingSource_AbortsAndKeepsTheOldObject()
    {
        var connection = _s3.Connect(Bucket, multipartThreshold: 6 * MiB, partSize: 5 * MiB);
        _s3.Put(Bucket, "keep.bin", [7]);

        var result = await new S3FileProvider(connection).CopyFileInAsync(
            new MemoryFileItem("keep.bin", RandomBytes(12 * MiB), failAfter: 7 * MiB), connection.FullName("keep.bin"),
            new Progress<double>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(_s3.OpenUploads, Is.Empty);
        Assert.That(_s3.Requests.Count(r => r.StartsWith("DELETE") && r.Contains("uploadId=")), Is.EqualTo(1));
        Assert.That(_s3.Get(Bucket, "keep.bin"), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public async Task Upload_Cancelled_Aborts()
    {
        var connection = _s3.Connect(Bucket, multipartThreshold: 6 * MiB, partSize: 5 * MiB);
        using var cts = new CancellationTokenSource();

        var result = await new S3FileProvider(connection).CopyFileInAsync(
            new MemoryFileItem("x.bin", RandomBytes(12 * MiB), onRead: (read, _) =>
            {
                if (read > 6 * MiB)
                    cts.Cancel();
            }), connection.FullName("x.bin"), new Progress<double>(), cts.Token);

        Assert.That(result.Exception, Is.InstanceOf<OperationCanceledException>());
        Assert.That(_s3.OpenUploads, Is.Empty);
        Assert.That(_s3.Get(Bucket, "x.bin"), Is.Null);
    }

    [Test]
    public async Task Copy_SameAccount_IsServerSide_EvenAcrossBuckets()
    {
        var source = _s3.Connect("source");
        var destination = _s3.Connect(Bucket);
        _s3.Put("source", "a/file.txt", [1, 2]);
        var item = (await new S3FileProvider(source, "a").GetItemsAsync(FileProviderFilter.Default).ToListAsync())
            .OfType<IFileItem>().Single();

        var result = await new S3FileProvider(destination).CopyFileInAsync(item, destination.FullName("copy.txt"),
            new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_s3.Get(Bucket, "copy.txt"), Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(_s3.Requests.Any(r => r.EndsWith(" copy")), Is.True);
        Assert.That(_s3.Requests.Any(r => r.StartsWith("GET /source/a/file.txt")), Is.False, "no download");
    }

    [Test]
    public async Task Copy_OverMultipartCopyThreshold_CopiesRanges()
    {
        var connection = _s3.Connect(Bucket, partSize: 5 * MiB, multipartCopyThreshold: 6 * MiB);
        var data = RandomBytes(13 * MiB);
        _s3.Put(Bucket, "big.bin", data);
        var provider = new S3FileProvider(connection);
        var item = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFileItem>().Single();

        var result = await provider.CopyFileInAsync(item, connection.FullName("big copy.bin"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        AssertContent("big copy.bin", data);
        Assert.That(_s3.Requests.Count(r => r.Contains("partNumber=") && r.EndsWith(" copy")), Is.EqualTo(3));
    }

    [Test]
    public async Task Copy_WithoutUploadPartCopy_FallsBackToStreaming()
    {
        _s3.SupportsUploadPartCopy = false;
        var connection = _s3.Connect(Bucket, multipartThreshold: 6 * MiB, partSize: 5 * MiB,
            multipartCopyThreshold: 6 * MiB);
        var data = RandomBytes(13 * MiB);
        _s3.Put(Bucket, "big.bin", data);
        var provider = new S3FileProvider(connection);
        var item = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFileItem>().Single();

        var result = await provider.CopyFileInAsync(item, connection.FullName("big copy.bin"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        AssertContent("big copy.bin", data);
        Assert.That(_s3.OpenUploads, Is.Empty);
    }

    [Test]
    public async Task RenameFolder_MovesEveryKey()
    {
        var connection = _s3.Connect(Bucket);
        _s3.Put(Bucket, "old/", []);
        _s3.Put(Bucket, "old/a.txt", [1]);
        _s3.Put(Bucket, "old/sub/b.txt", [2]);
        _s3.Put(Bucket, "older.txt", [3]);
        var provider = new S3FileProvider(connection);
        var folder = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFolderItem>().Single();

        var result = await provider.RenameAsync(folder, "new");

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_s3.Keys(Bucket), Is.EqualTo(new[] { "new/", "new/a.txt", "new/sub/b.txt", "older.txt" }));
        Assert.That(_s3.Requests.Count(r => r.Contains("?delete=")), Is.EqualTo(1), "one DeleteObjects batch");
    }

    [Test]
    public async Task Rename_ToATakenName_Fails()
    {
        var provider = new S3FileProvider(_s3.Connect(Bucket));
        _s3.Put(Bucket, "a.txt", [1]);
        _s3.Put(Bucket, "b.txt/x", [2]);
        var file = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFileItem>().Single();

        var result = await provider.RenameAsync(file, "b.txt");

        Assert.That(result.Exception, Is.InstanceOf<IOException>());
        Assert.That(_s3.Get(Bucket, "a.txt"), Is.Not.Null);
    }

    [Test]
    public async Task MoveFolder_IntoItself_Fails()
    {
        var connection = _s3.Connect(Bucket);
        _s3.Put(Bucket, "a/x.txt", [1]);
        var provider = new S3FileProvider(connection);
        var folder = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFolderItem>().Single();

        var result = await provider.TryMoveFolderAsync(folder, connection.FullName("a/inner"), new Progress<double>());

        Assert.That(result.Exception, Is.InstanceOf<IOException>());
        Assert.That(_s3.Keys(Bucket), Is.EqualTo(new[] { "a/x.txt" }));
    }

    [Test]
    public async Task CopyFolder_SameAccount_CopiesOnTheServer()
    {
        var connection = _s3.Connect(Bucket);
        _s3.Put(Bucket, "src/a.txt", [1]);
        _s3.Put(Bucket, "src/deep/b.txt", [2]);
        var provider = new S3FileProvider(connection);
        var folder = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFolderItem>().Single();

        var result = await provider.CopyFolderInAsync(folder, connection.FullName("dst"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result!.FullName, Is.EqualTo(connection.FullName("dst")));
        Assert.That(_s3.Keys(Bucket), Is.EqualTo(new[] { "dst/a.txt", "dst/deep/b.txt", "src/a.txt", "src/deep/b.txt" }));
    }

    [Test]
    public async Task CopyFolder_FromElsewhere_AsksToRecurse()
    {
        var provider = new S3FileProvider(_s3.Connect(Bucket));
        var other = new FakeS3();
        var otherConnection = new S3Connection
        {
            Endpoint = new Uri("http://other.test"), Bucket = Bucket, AccessKeyId = "other", SecretAccessKey = "x"
        };

        var result = await provider.CopyFolderInAsync(new S3FolderItem(otherConnection, "x", DateTimeOffset.Now),
            provider.Connection.FullName("x"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Result, Is.Null);
        other.Dispose();
    }

    [Test]
    public async Task Delete_ToRecycleBin_IsRefusedAndTouchesNothing()
    {
        var provider = new S3FileProvider(_s3.Connect(Bucket));
        _s3.Put(Bucket, "a.txt", [1]);
        var items = await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync();
        var requests = _s3.Requests.Count;

        var result = await provider.DeleteAsync(items, toRecycleBin: true);

        Assert.That(result.Exception, Is.InstanceOf<NotSupportedException>());
        Assert.That(_s3.Requests, Has.Count.EqualTo(requests));
        Assert.That(_s3.Get(Bucket, "a.txt"), Is.Not.Null);
    }

    [Test]
    public async Task Delete_Folder_BatchesByThousand()
    {
        var provider = new S3FileProvider(_s3.Connect(Bucket));
        _s3.Put(Bucket, "many/", []);
        for (var i = 0; i < 1500; i++)
            _s3.Put(Bucket, $"many/{i:D4}.txt", []);
        _s3.Put(Bucket, "stay.txt", []);
        var folder = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFolderItem>().Single();

        var result = await provider.DeleteAsync([folder], toRecycleBin: false);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(_s3.Keys(Bucket), Is.EqualTo(new[] { "stay.txt" }));
        Assert.That(_s3.Requests.Count(r => r.Contains("?delete=")), Is.EqualTo(2));
    }

    [Test]
    public async Task Delete_ItemOfAnotherBucket_IsRefused()
    {
        var provider = new S3FileProvider(_s3.Connect(Bucket));
        var foreign = new S3FileItem(_s3.Connect("other"), "a.txt", 1, DateTimeOffset.Now, null);

        var result = await provider.DeleteAsync([foreign], toRecycleBin: false);

        Assert.That(result.Exception, Is.InstanceOf<ArgumentException>());
    }

    [Test]
    public async Task Search_FindsFilesAndImpliedFoldersOnce_IgnoringCase()
    {
        _s3.PageSize = 2;
        var provider = new S3FileProvider(_s3.Connect(Bucket), "root");
        _s3.Put(Bucket, "root/Отчёты/", []);
        _s3.Put(Bucket, "root/Отчёты/2024/отчёт.pdf", [1]);
        _s3.Put(Bucket, "root/Отчёты/2024/summary.txt", [1]);
        _s3.Put(Bucket, "root/other/ОТЧЁТ-final.docx", [1]);
        _s3.Put(Bucket, "root/other/notes.txt", [1]);
        _s3.Put(Bucket, "outside/отчёт.pdf", [1]);

        var found = await provider.SearchAsync("отчёт").ToListAsync();

        Assert.That(found.Select(i => i.FullName), Is.EquivalentTo(new[]
        {
            "s3://s3.fake.test/bucket/root/Отчёты",
            "s3://s3.fake.test/bucket/root/Отчёты/2024/отчёт.pdf",
            "s3://s3.fake.test/bucket/root/other/ОТЧЁТ-final.docx"
        }));
        Assert.That(found.OfType<S3FolderItem>().Single().Path, Is.EqualTo("root/Отчёты"));
        Assert.That(found.OfType<S3FileItem>().First().Size, Is.EqualTo(1));
    }

    [Test]
    public async Task Errors_CarryS3Codes()
    {
        var connection = new S3Connection
        {
            Endpoint = new Uri($"http://{FakeS3.EndpointHost}"), Bucket = Bucket, AccessKeyId = FakeS3.AccessKey,
            SecretAccessKey = "wrong", ForcePathStyle = true, HttpClient = new HttpClient(_s3, false)
        };
        _s3.Connect(Bucket);

        var result = await new S3FileProvider(connection).CreateFolderAsync("x");

        Assert.That(result.Exception, Is.InstanceOf<S3Exception>());
        Assert.That(((S3Exception)result.Exception!).Code, Is.EqualTo("SignatureDoesNotMatch"));
        Assert.ThrowsAsync<S3Exception>(() => new S3FileProvider(connection).ContainsFileAsync("x"));
    }

    // NUnit compares arrays element by element, which takes seconds for megabytes
    private void AssertContent(string key, byte[] expected)
    {
        var actual = _s3.Get(Bucket, key);
        Assert.That(actual, Is.Not.Null, key);
        Assert.That(actual!.AsSpan().SequenceEqual(expected), Is.True, $"content of {key}");
    }

    private static byte[] RandomBytes(long length)
    {
        var data = new byte[length];
        Random.Shared.NextBytes(data);
        return data;
    }

    private sealed class SyncProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }
}
