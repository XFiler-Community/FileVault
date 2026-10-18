using FileVault.Core;
using FileVault.Local;

namespace FileVault.Local.Tests;

[TestFixture]
public class LocalFileProviderCopyCancellationTests
{
    private const int ChunkSize = 1024;

    private string _rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_rootDir))
            Directory.Delete(_rootDir, recursive: true);
    }

    [Test]
    public async Task CopyFileInAsync_SourceCancelsHalfway_LeavesNoFileAndNoTempFile()
    {
        var provider = new LocalFileProvider(_rootDir);
        var source = new FakeFileItem("big.bin", chunks: 4, throwAfterChunks: 2);
        var dest = await provider.ResolveDestinationFileAsync(source, overwrite: false);

        var result = await provider.CopyFileInAsync(source, dest.FullName, new Progress<double>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.InstanceOf<OperationCanceledException>());
        Assert.That(Directory.EnumerateFileSystemEntries(_rootDir), Is.Empty);
    }

    [Test]
    public async Task CopyFileInAsync_TokenCancelledMidCopy_LeavesNoFileAndNoTempFile()
    {
        var provider = new LocalFileProvider(_rootDir);
        using var cts = new CancellationTokenSource();
        var source = new FakeFileItem("big.bin", chunks: 4, onChunkRead: n => { if (n == 2) cts.Cancel(); });
        var dest = await provider.ResolveDestinationFileAsync(source, overwrite: false);

        var result = await provider.CopyFileInAsync(source, dest.FullName, new Progress<double>(), cts.Token);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.InstanceOf<OperationCanceledException>());
        Assert.That(Directory.EnumerateFileSystemEntries(_rootDir), Is.Empty);
    }

    [Test]
    public async Task CopyFileInAsync_PreCancelledToken_LeavesNoFile()
    {
        var provider = new LocalFileProvider(_rootDir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var source = new FakeFileItem("big.bin", chunks: 4);
        var destinationPath = Path.Combine(_rootDir, "big.bin");

        var result = await provider.CopyFileInAsync(source, destinationPath, new Progress<double>(), cts.Token);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(Directory.EnumerateFileSystemEntries(_rootDir), Is.Empty);
    }

    [Test]
    public async Task CopyFileInAsync_ReplaceExisting_OverwritesContent()
    {
        var destinationPath = Path.Combine(_rootDir, "doc.bin");
        await File.WriteAllTextAsync(destinationPath, "old content");
        var provider = new LocalFileProvider(_rootDir);
        var source = new FakeFileItem("doc.bin", chunks: 3);
        var dest = await provider.ResolveDestinationFileAsync(source, overwrite: true);
        Assert.That(dest.FullName, Is.EqualTo(destinationPath));

        var result = await provider.CopyFileInAsync(source, dest.FullName, new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(await File.ReadAllBytesAsync(destinationPath), Is.EqualTo(FakeFileItem.ExpectedContent(3)));
        Assert.That(Directory.EnumerateFileSystemEntries(_rootDir), Is.EquivalentTo(new[] { destinationPath }));
    }

    [Test]
    public async Task CopyFileInAsync_ReplaceExistingFails_KeepsOldContent()
    {
        var destinationPath = Path.Combine(_rootDir, "doc.bin");
        await File.WriteAllTextAsync(destinationPath, "old content");
        var provider = new LocalFileProvider(_rootDir);
        var source = new FakeFileItem("doc.bin", chunks: 4, throwAfterChunks: 2, failure: new IOException("network lost"));

        var result = await provider.CopyFileInAsync(source, destinationPath, new Progress<double>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(await File.ReadAllTextAsync(destinationPath), Is.EqualTo("old content"));
        Assert.That(Directory.EnumerateFileSystemEntries(_rootDir), Is.EquivalentTo(new[] { destinationPath }));
    }

    [Test]
    public async Task MoveFileInAsync_CrossProviderCancelled_KeepsSourceAndLeavesNoFile()
    {
        var provider = new LocalFileProvider(_rootDir);
        var source = new FakeFileItem("big.bin", chunks: 4, throwAfterChunks: 2);

        var result = await provider.MoveFileInAsync(source, Path.Combine(_rootDir, "big.bin"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(source.Deleted, Is.False);
        Assert.That(Directory.EnumerateFileSystemEntries(_rootDir), Is.Empty);
    }

    [Test]
    public async Task CopyFolderInAsync_LocalSource_LeavesNoTempFiles()
    {
        var sourceDir = Path.Combine(_rootDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "a.bin"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(sourceDir, "nested", "b.bin"), [4, 5]);
        var destRoot = Path.Combine(_rootDir, "dst");
        Directory.CreateDirectory(destRoot);

        var source = (await new LocalFileProvider(_rootDir).GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync())
            .OfType<IFolderItem>().Single(f => f.Name == "src");
        var provider = new LocalFileProvider(destRoot);

        var result = await provider.CopyFolderInAsync(source, Path.Combine(destRoot, "src"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        var copied = Directory.EnumerateFiles(destRoot, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(destRoot, p));
        Assert.That(copied, Is.EquivalentTo(new[] { Path.Combine("src", "a.bin"), Path.Combine("src", "nested", "b.bin") }));
    }

    private sealed class FakeFileItem(
        string name,
        int chunks,
        int? throwAfterChunks = null,
        Exception? failure = null,
        Action<int>? onChunkRead = null) : IFileItem
    {
        public bool Deleted { get; private set; }

        public string Name => name;
        public string FullName => $"gdrive:/root/{name}";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        public long Size => chunks * ChunkSize;
        long? IFileProviderItem.Size => Size;
        public string Extension => Path.GetExtension(name);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

        public static byte[] ExpectedContent(int chunks)
            => Enumerable.Range(0, chunks).SelectMany(i => Enumerable.Repeat((byte)(i + 1), ChunkSize)).ToArray();

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Stream stream = new ChunkedStream(chunks, throwAfterChunks, failure ?? new OperationCanceledException(), onChunkRead);
            return Task.FromResult((stream, Size));
        }

        public Task DeleteAsync(CancellationToken ct = default)
        {
            Deleted = true;
            return Task.CompletedTask;
        }
    }

    // Returns one chunk per read, so the copy loop writes some data before the failure hits.
    private sealed class ChunkedStream(int chunks, int? throwAfterChunks, Exception failure, Action<int>? onChunkRead) : Stream
    {
        private int _chunksRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => chunks * ChunkSize;
        public override long Position { get => _chunksRead * ChunkSize; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_chunksRead == throwAfterChunks)
                throw failure;
            if (_chunksRead == chunks)
                return ValueTask.FromResult(0);

            var count = Math.Min(ChunkSize, buffer.Length);
            buffer.Span[..count].Fill((byte)(_chunksRead + 1));
            _chunksRead++;
            onChunkRead?.Invoke(_chunksRead);
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
