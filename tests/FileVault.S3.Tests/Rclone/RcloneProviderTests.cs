using FileVault.Core;

namespace FileVault.S3.Tests.Rclone;

/// <summary>S3 behaviour beyond the contract against <c>rclone serve s3</c>, each test in its own folder.</summary>
[TestFixture]
[Category("Integration")]
public class RcloneProviderTests
{
    private const long MiB = 1024 * 1024;

    private string _prefix = null!;

    private string Folder => Path.Combine(RcloneS3Server.BucketFolder, _prefix);

    [SetUp]
    public void SetUp()
    {
        _prefix = $"provider-{Guid.NewGuid():N}";
        Directory.CreateDirectory(Folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, recursive: true);
    }

    [TestCase("dir a", "f+1%.txt")]
    [TestCase("Папка с пробелом", "Файл ё.txt")]
    [TestCase("q&=#'()!", "x~y $@,;.bin")]
    public async Task Keys_WithSpecialCharacters_RoundTrip(string folderName, string fileName)
    {
        var connection = RcloneS3Server.Connect();
        var provider = new S3FileProvider(connection, _prefix);
        var key = $"{_prefix}/{folderName}/{fileName}";

        var copy = await provider.CopyFileInAsync(new MemoryFileItem("src", [1, 2, 3]), connection.FullName(key),
            new Progress<double>());

        Assert.That(copy.IsSuccess, Is.True, copy.ErrorMessage);
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Folder, folderName, fileName)), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(copy.Result!.FullName, Is.EqualTo(connection.FullName(key)));
        Assert.That(copy.Result.Size, Is.EqualTo(3));

        var folder = (await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).OfType<IFolderItem>().Single();
        Assert.That(folder.Name, Is.EqualTo(folderName));
        var inner = folder.CreateProvider();
        Assert.That(await inner.ContainsFileAsync(fileName), Is.True);

        var file = (await inner.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).OfType<IFileItem>().Single();
        Assert.That(file.Name, Is.EqualTo(fileName));
        Assert.That(await ReadAllAsync(file), Is.EqualTo(new byte[] { 1, 2, 3 }));

        var renamed = await inner.RenameAsync(file, "renamed " + fileName);
        Assert.That(renamed.IsSuccess, Is.True, renamed.ErrorMessage);
        Assert.That(File.Exists(Path.Combine(Folder, folderName, "renamed " + fileName)), Is.True);
        Assert.That(File.Exists(Path.Combine(Folder, folderName, fileName)), Is.False);
    }

    // rclone gives a multipart object the MD5 of its content as ETag, so the parts are not visible here; the
    // in-memory S3 counts them
    [Test]
    public async Task Upload_KnownLengthOverThreshold_IsMultipart()
    {
        var connection = RcloneS3Server.Connect(multipartThreshold: 6 * MiB, partSize: 5 * MiB);
        var data = RandomBytes(12 * MiB + 123);

        var result = await new S3FileProvider(connection, _prefix).CopyFileInAsync(new MemoryFileItem("big.bin", data),
            connection.FullName($"{_prefix}/big.bin"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result!.Size, Is.EqualTo(data.Length));
        Assert.That((await File.ReadAllBytesAsync(Path.Combine(Folder, "big.bin"))).AsSpan().SequenceEqual(data), Is.True);
    }

    [Test]
    public async Task Upload_UnknownLength_IsMultipart()
    {
        var connection = RcloneS3Server.Connect(multipartThreshold: 6 * MiB, partSize: 5 * MiB);
        var data = RandomBytes(11 * MiB);

        var result = await new S3FileProvider(connection, _prefix).CopyFileInAsync(
            new MemoryFileItem("stream.bin", data, reportLength: false), connection.FullName($"{_prefix}/stream.bin"),
            new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That((await File.ReadAllBytesAsync(Path.Combine(Folder, "stream.bin"))).AsSpan().SequenceEqual(data), Is.True);
    }

    [Test]
    public async Task Upload_FailingSource_KeepsTheOldFile()
    {
        var connection = RcloneS3Server.Connect(multipartThreshold: 6 * MiB, partSize: 5 * MiB);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "keep.bin"), [7]);

        var result = await new S3FileProvider(connection, _prefix).CopyFileInAsync(
            new MemoryFileItem("keep.bin", RandomBytes(12 * MiB), failAfter: 7 * MiB),
            connection.FullName($"{_prefix}/keep.bin"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Folder, "keep.bin")), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public async Task Download_StreamsWithLength()
    {
        var data = RandomBytes(3 * MiB);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "down.bin"), data);
        var provider = new S3FileProvider(RcloneS3Server.Connect(), _prefix);
        var file = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFileItem>().Single();

        var (stream, length) = await file.OpenReadAsync();

        await using (stream)
        {
            Assert.That(length, Is.EqualTo(data.Length));
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.That(buffer.ToArray().AsSpan().SequenceEqual(data), Is.True);
        }
    }

    [Test]
    public async Task Copy_OverMultipartCopyThreshold_FallsBackToStreaming()
    {
        // rclone serve s3 has no UploadPartCopy, so a large copy streams through the client instead
        var connection = RcloneS3Server.Connect(multipartThreshold: 6 * MiB, partSize: 5 * MiB,
            multipartCopyThreshold: 6 * MiB);
        var data = RandomBytes(13 * MiB);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "big.bin"), data);
        var provider = new S3FileProvider(connection, _prefix);
        var file = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFileItem>().Single();

        var result = await provider.CopyFileInAsync(file, connection.FullName($"{_prefix}/big copy.bin"),
            new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That((await File.ReadAllBytesAsync(Path.Combine(Folder, "big copy.bin"))).AsSpan().SequenceEqual(data),
            Is.True);
    }

    [Test]
    public async Task Folder_CopyRenameDelete_HandleEveryKey()
    {
        var connection = RcloneS3Server.Connect();
        Directory.CreateDirectory(Path.Combine(Folder, "src", "deep"));
        await File.WriteAllBytesAsync(Path.Combine(Folder, "src", "a.txt"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "src", "deep", "b.txt"), [2]);
        var provider = new S3FileProvider(connection, _prefix);
        var source = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFolderItem>().Single();

        var copy = await provider.CopyFolderInAsync(source, connection.FullName($"{_prefix}/copy"), new Progress<double>());
        Assert.That(copy.IsSuccess, Is.True, copy.ErrorMessage);
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Folder, "copy", "deep", "b.txt")), Is.EqualTo(new byte[] { 2 }));
        Assert.That(File.Exists(Path.Combine(Folder, "src", "a.txt")), Is.True);

        var rename = await provider.RenameAsync(copy.Result!, "renamed");
        Assert.That(rename.IsSuccess, Is.True, rename.ErrorMessage);
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(Folder, "renamed", "a.txt")), Is.EqualTo(new byte[] { 1 }));
        Assert.That(Directory.Exists(Path.Combine(Folder, "copy")), Is.False);

        var move = await provider.TryMoveFolderAsync(source, connection.FullName($"{_prefix}/renamed/moved"),
            new Progress<double>());
        Assert.That(move.IsSuccess, Is.True, move.ErrorMessage);
        Assert.That(File.Exists(Path.Combine(Folder, "renamed", "moved", "deep", "b.txt")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(Folder, "src")), Is.False);

        var renamed = (await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync()).OfType<IFolderItem>().Single();
        var refused = await provider.DeleteAsync([renamed], toRecycleBin: true);
        Assert.That(refused.Exception, Is.InstanceOf<NotSupportedException>());
        Assert.That(File.Exists(Path.Combine(Folder, "renamed", "a.txt")), Is.True);

        var delete = await provider.DeleteAsync([renamed], toRecycleBin: false);
        Assert.That(delete.IsSuccess, Is.True, delete.ErrorMessage);
        Assert.That(await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync(), Is.Empty);
    }

    [Test]
    public async Task Search_FindsFilesAndImpliedFolders()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "Отчёты", "2024"));
        Directory.CreateDirectory(Path.Combine(Folder, "other"));
        await File.WriteAllBytesAsync(Path.Combine(Folder, "Отчёты", "2024", "отчёт.pdf"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "Отчёты", "2024", "summary.txt"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "other", "ОТЧЁТ final.docx"), [1]);
        var connection = RcloneS3Server.Connect();

        var found = await new S3FileProvider(connection, _prefix).SearchAsync("отчёт").ToListAsync();

        Assert.That(found.Select(i => i.FullName), Is.EquivalentTo(new[]
        {
            connection.FullName($"{_prefix}/Отчёты"),
            connection.FullName($"{_prefix}/Отчёты/2024/отчёт.pdf"),
            connection.FullName($"{_prefix}/other/ОТЧЁТ final.docx")
        }));
    }

    [Test]
    public async Task WrongSecret_FailsWithS3Code()
    {
        var good = RcloneS3Server.Connect();
        var bad = new S3Connection
        {
            Endpoint = good.Endpoint, Bucket = good.Bucket, AccessKeyId = good.AccessKeyId,
            SecretAccessKey = "wrong", ForcePathStyle = true
        };

        var result = await new S3FileProvider(bad, _prefix).CreateEmptyTextFileAsync("x.txt");

        Assert.That(result.Exception, Is.InstanceOf<S3Exception>());
        Assert.That(((S3Exception)result.Exception!).Code, Is.EqualTo("SignatureDoesNotMatch"));
    }

    private static async Task<byte[]> ReadAllAsync(IFileItem file)
    {
        var (stream, _) = await file.OpenReadAsync();

        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            return buffer.ToArray();
        }
    }

    private static byte[] RandomBytes(long length)
    {
        var data = new byte[length];
        Random.Shared.NextBytes(data);
        return data;
    }
}
