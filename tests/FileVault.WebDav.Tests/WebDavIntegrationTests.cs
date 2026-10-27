using System.Net;
using FileVault.Core;

namespace FileVault.WebDav.Tests;

/// <summary>What the contract does not cover, against <c>rclone serve webdav</c>, checked on the served disk.</summary>
[TestFixture]
[Category("Integration")]
public class WebDavIntegrationTests
{
    private const string AwkwardName = "Отчёт #1 100% a+b (копия).txt";

    private RcloneWebDavServer _server = null!;
    private WebDavConnection _connection = null!;
    private string _folder = null!;
    private WebDavFileProvider _provider = null!;

    private string Local(params string[] parts) => Path.Combine([_server.RootDirectory, _folder, .. parts]);

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await RcloneWebDavServer.StartOrIgnoreAsync();
        _connection = _server.CreateConnection();
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        _connection?.Dispose();

        if (_server is not null)
            await _server.DisposeAsync();
    }

    [SetUp]
    public void SetUp()
    {
        _folder = $"run {Guid.NewGuid():N}";
        Directory.CreateDirectory(Local());
        _provider = new WebDavFileProvider(_connection, _folder);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(Local(), recursive: true);

    [Test]
    public async Task AwkwardNames_RoundTrip_ThroughUploadListAndDownload()
    {
        var folder = await _provider.CreateFolderAsync("Папка #2 50%");
        Assert.That(folder.IsSuccess, Is.True, folder.ErrorMessage);

        var inner = folder.Result!.CreateProvider();
        byte[] data = [1, 2, 3, 4, 5, 6, 7];
        var upload = await inner.CopyFileInAsync(new MemoryFile(AwkwardName, data), $"{folder.Result.FullName}/{AwkwardName}",
            new Progress<double>());
        Assert.That(upload.IsSuccess, Is.True, upload.ErrorMessage);

        Assert.That(File.ReadAllBytes(Local("Папка #2 50%", AwkwardName)), Is.EqualTo(data));
        Assert.That(Directory.GetFiles(Local("Папка #2 50%")), Has.Length.EqualTo(1), "no partial file left");

        var items = await inner.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var file = items.OfType<IFileItem>().Single();
        Assert.That(file.Name, Is.EqualTo(AwkwardName));
        Assert.That(file.Size, Is.EqualTo(data.Length));
        Assert.That(file.FullName, Is.EqualTo($"webdav://127.0.0.1:{_server.BaseUri.Port}/dav/{_folder}/Папка #2 50%/{AwkwardName}"));

        var (stream, length) = await file.OpenReadAsync();
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(data));
            Assert.That(length, Is.EqualTo(data.Length));
        }

        Assert.That(await inner.ContainsFileAsync(AwkwardName), Is.True);
        Assert.That(await inner.ContainsFolderAsync(AwkwardName), Is.False);
        Assert.That(await _provider.ContainsFolderAsync("Папка #2 50%"), Is.True);
    }

    [Test]
    public async Task UnknownLengthUpload_IsBufferedAndComplete()
    {
        var data = Enumerable.Range(0, 3_000_000).Select(i => (byte)i).ToArray();
        var progress = new List<double>();

        var result = await _provider.CopyFileInAsync(new MemoryFile("big.bin", data, unknownLength: true),
            _connection.GetFullName($"{_folder}/big.bin"), new SyncProgress(progress.Add));

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(File.ReadAllBytes(Local("big.bin")).SequenceEqual(data), Is.True);
        Assert.That(progress.Last(), Is.EqualTo(1.0));
    }

    [Test]
    public async Task Replace_OverwritesContent_AndLeavesNoPartialFile()
    {
        File.WriteAllBytes(Local("r.txt"), [9, 9, 9]);

        var result = await _provider.CopyFileInAsync(new MemoryFile("r.txt", [1]), _connection.GetFullName($"{_folder}/r.txt"),
            new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(File.ReadAllBytes(Local("r.txt")), Is.EqualTo(new byte[] { 1 }));
        Assert.That(Directory.GetFiles(Local()), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task CancelledUpload_LeavesNeitherPartialFileNorDamagedOriginal()
    {
        File.WriteAllBytes(Local("keep.bin"), [7, 7]);
        using var cts = new CancellationTokenSource();
        var source = new MemoryFile("keep.bin", new byte[4_000_000], unknownLength: false, afterFirstRead: cts.Cancel);

        var result = await _provider.CopyFileInAsync(source, _connection.GetFullName($"{_folder}/keep.bin"),
            new Progress<double>(), cts.Token);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(File.ReadAllBytes(Local("keep.bin")), Is.EqualTo(new byte[] { 7, 7 }));
        Assert.That(Directory.GetFiles(Local()).Select(Path.GetFileName), Is.EqualTo(new[] { "keep.bin" }));
    }

    [Test]
    public async Task SameServerFileCopy_OverExistingFile_Replaces()
    {
        File.WriteAllBytes(Local("a.txt"), [1, 2]);
        File.WriteAllBytes(Local("b.txt"), [3]);
        var a = (await _provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).OfType<IFileItem>().Single(i => i.Name == "a.txt");

        var result = await _provider.CopyFileInAsync(a, _connection.GetFullName($"{_folder}/b.txt"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(File.ReadAllBytes(Local("b.txt")), Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(Directory.GetFiles(Local()), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task FileOverFolder_IsRefused()
    {
        Directory.CreateDirectory(Local("x"));
        File.WriteAllText(Local("x", "inside.txt"), "keep");

        var result = await _provider.CopyFileInAsync(new MemoryFile("x", [1]), _connection.GetFullName($"{_folder}/x"),
            new Progress<double>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(File.ReadAllText(Local("x", "inside.txt")), Is.EqualTo("keep"));
        Assert.That(Directory.GetFiles(Local()), Is.Empty, "the uploaded temp file is removed");
    }

    [Test]
    public async Task FolderCopyAndMove_AreNative_ExistingDestinationMeansRecurse()
    {
        Directory.CreateDirectory(Local("src #1", "deep"));
        File.WriteAllText(Local("src #1", "deep", "f.txt"), "hello");
        var source = (await _provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).OfType<IFolderItem>().Single();

        var copy = await _provider.CopyFolderInAsync(source, _connection.GetFullName($"{_folder}/copy %"), new Progress<double>());
        Assert.That(copy.IsSuccess, Is.True, copy.ErrorMessage);
        Assert.That(copy.Result, Is.Not.Null);
        Assert.That(File.ReadAllText(Local("copy %", "deep", "f.txt")), Is.EqualTo("hello"));

        var again = await _provider.CopyFolderInAsync(source, _connection.GetFullName($"{_folder}/copy %"), new Progress<double>());
        Assert.That(again.IsSuccess, Is.True, again.ErrorMessage);
        Assert.That(again.Result, Is.Null, "an existing folder is merged by the caller, never replaced");

        var inside = await _provider.CopyFolderInAsync(source, _connection.GetFullName($"{_folder}/src #1/deep/x"), new Progress<double>());
        Assert.That(inside.IsSuccess, Is.False);

        var move = await _provider.TryMoveFolderAsync(source, _connection.GetFullName($"{_folder}/moved"), new Progress<double>());
        Assert.That(move.IsSuccess, Is.True, move.ErrorMessage);
        Assert.That(move.Result, Is.Not.Null);
        Assert.That(Directory.Exists(Local("src #1")), Is.False);
        Assert.That(File.ReadAllText(Local("moved", "deep", "f.txt")), Is.EqualTo("hello"));
    }

    [Test]
    public async Task ItemsOfAnotherConnection_AreNotMovedNatively()
    {
        File.WriteAllBytes(Local("o.txt"), [5]);
        using var other = _server.CreateConnection();
        var foreign = (await new WebDavFileProvider(other, _folder).GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync())
            .OfType<IFolderItem>().ToList();
        Directory.CreateDirectory(Local("f"));
        var foreignFolder = (await new WebDavFileProvider(other, _folder).GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync())
            .OfType<IFolderItem>().Single();

        var result = await _provider.TryMoveFolderAsync(foreignFolder, _connection.GetFullName($"{_folder}/g"), new Progress<double>());

        Assert.That(foreign, Is.Empty);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Result, Is.Null);
        Assert.That(Directory.Exists(Local("f")), Is.True);
    }

    [Test]
    public async Task RenameFolder_AndConflict()
    {
        Directory.CreateDirectory(Local("a"));
        Directory.CreateDirectory(Local("b"));
        var a = (await _provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single(i => i.Name == "a");

        var conflict = await _provider.RenameAsync(a, "b");
        Assert.That(conflict.IsSuccess, Is.False);
        Assert.That(Directory.Exists(Local("a")), Is.True);

        var renamed = await _provider.RenameAsync(a, "Новое имя #");
        Assert.That(renamed.IsSuccess, Is.True, renamed.ErrorMessage);
        Assert.That(Directory.Exists(Local("Новое имя #")), Is.True);
    }

    [Test]
    public async Task DeleteToRecycleBin_IsRefused_AndDeletesNothing()
    {
        File.WriteAllBytes(Local("stay.txt"), [1]);
        var items = await _provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await _provider.DeleteAsync(items, toRecycleBin: true);

        Assert.That(result.Exception, Is.InstanceOf<NotSupportedException>());
        Assert.That(File.Exists(Local("stay.txt")), Is.True);
    }

    [Test]
    public async Task DeleteFolder_RemovesEverythingInIt()
    {
        Directory.CreateDirectory(Local("d", "e"));
        File.WriteAllBytes(Local("d", "e", "f"), [1]);
        var items = await _provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await _provider.DeleteAsync(items, toRecycleBin: false);

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(Directory.Exists(Local("d")), Is.False);
    }

    [Test]
    public async Task Drive_OpensTheBase_WithoutQuotaFromRclone()
    {
        var drive = await _connection.GetDriveAsync();

        Assert.That(drive.FullName, Is.EqualTo($"webdav://127.0.0.1:{_server.BaseUri.Port}/dav"));
        Assert.That(drive.Name, Is.EqualTo("127.0.0.1"));
        Assert.That(drive.TotalSize, Is.GreaterThanOrEqualTo(0));

        var names = await drive.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).Select(i => i.Name).ToListAsync();
        Assert.That(names, Does.Contain(_folder));
    }

    [Test]
    public void WrongPassword_Is401()
    {
        using var wrong = _server.CreateConnection(password: "nope");
        var ex = Assert.ThrowsAsync<WebDavException>(() => wrong.GetDriveAsync());
        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public void MissingFolder_ListingThrows_ContainsIsFalse()
    {
        var missing = new WebDavFileProvider(_connection, $"{_folder}/nope");
        Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await missing.GetItemsAsync(FileProviderFilter.Default).ToListAsync());
        Assert.That(_provider.ContainsFolderAsync("nope").Result, Is.False);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>A file of another provider; unknownLength reports 0 bytes over a non-seekable stream.</summary>
    private sealed class MemoryFile(string name, byte[] data, bool unknownLength = false, Action? afterFirstRead = null) : IFileItem
    {
        public string Name => name;
        public string FullName => "/elsewhere/" + name;
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        long? IFileProviderItem.Size => data.Length;
        public long Size => data.Length;
        public string Extension => Path.GetExtension(name);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
            Task.FromResult<(Stream, long)>((new ForwardOnlyStream(new MemoryStream(data), afterFirstRead),
                unknownLength ? 0 : data.Length));

        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ForwardOnlyStream(Stream inner, Action? afterFirstRead) : Stream
    {
        private bool _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().Result;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], ct);

            if (!_read)
            {
                _read = true;
                afterFirstRead?.Invoke();
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
