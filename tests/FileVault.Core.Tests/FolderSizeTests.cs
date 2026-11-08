using System.Runtime.CompilerServices;
using FileVault.Core;

namespace FileVault.Core.Tests;

[TestFixture]
public class FolderSizeTests
{
    private TimeSpan _reportInterval;

    [SetUp]
    public void SetUp() => _reportInterval = FolderSizeWalker.ReportInterval;

    [TearDown]
    public void TearDown() => FolderSizeWalker.ReportInterval = _reportInterval;

    // ── FolderSizeTally ─────────────────────────────────────────────────────

    [Test]
    public void Tally_AddsUpPerSubfolder()
    {
        var tally = new FolderSizeTally();

        tally.Add("a", 3);
        tally.Add("a", 5);
        tally.Add("b", 1);
        var sizes = tally.Snapshot(isComplete: true);

        Assert.That(sizes.Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 8, ["b"] = 1 }));
        Assert.That(sizes.IsComplete, Is.True);
        Assert.That(sizes.Find("a"), Is.EqualTo(8));
        Assert.That(sizes.Find("missing"), Is.Null);
    }

    [Test]
    public void Tally_AddFolder_KnowsEmptyFolder_AndKeepsBytesAlreadyAdded()
    {
        var tally = new FolderSizeTally();

        tally.AddFolder("empty");
        tally.Add("a", 4);
        tally.AddFolder("a");
        var sizes = tally.Snapshot(isComplete: false);

        Assert.That(sizes.Find("empty"), Is.Zero);
        Assert.That(sizes.Find("a"), Is.EqualTo(4));
        Assert.That(sizes.IsComplete, Is.False);
    }

    [Test]
    public void Tally_NegativeBytes_AreIgnored()
    {
        var tally = new FolderSizeTally();

        tally.Add("a", 10);
        tally.Add("a", -7);
        tally.Add("b", -1);
        var sizes = tally.Snapshot(isComplete: true);

        Assert.That(sizes.Find("a"), Is.EqualTo(10));
        Assert.That(sizes.Find("b"), Is.Zero);
    }

    [Test]
    public void Tally_HasChanged_TracksAddsSinceLastSnapshot()
    {
        var tally = new FolderSizeTally();
        Assert.That(tally.HasChanged, Is.True, "nothing reported yet");

        tally.Snapshot(isComplete: false);
        Assert.That(tally.HasChanged, Is.False);

        tally.Add("a", 0);
        Assert.That(tally.HasChanged, Is.True);

        tally.Snapshot(isComplete: false);
        tally.AddFolder("b");
        Assert.That(tally.HasChanged, Is.True, "a new folder is a change");

        tally.Snapshot(isComplete: false);
        tally.AddFolder("b");
        Assert.That(tally.HasChanged, Is.False, "a folder already known is not");
    }

    [Test]
    public void Tally_Comparer_MergesNamesAndCarriesIntoSnapshot()
    {
        var tally = new FolderSizeTally(StringComparer.OrdinalIgnoreCase);

        tally.Add("Photos", 2);
        tally.Add("photos", 3);
        var sizes = tally.Snapshot(isComplete: true);

        Assert.That(sizes.Bytes, Has.Count.EqualTo(1));
        Assert.That(sizes.Find("PHOTOS"), Is.EqualTo(5));
    }

    [Test]
    public void Tally_DefaultComparer_IsOrdinal()
    {
        var tally = new FolderSizeTally();

        tally.Add("Photos", 2);
        tally.Add("photos", 3);
        var sizes = tally.Snapshot(isComplete: true);

        Assert.That(sizes.Find("Photos"), Is.EqualTo(2));
        Assert.That(sizes.Find("photos"), Is.EqualTo(3));
    }

    [Test]
    public void Tally_Snapshot_IsACopy()
    {
        var tally = new FolderSizeTally();
        tally.Add("a", 1);
        var first = tally.Snapshot(isComplete: false);

        tally.Add("a", 1);
        tally.Add("b", 1);

        Assert.That(first.Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 1 }));
    }

    // ── SubfolderOf ─────────────────────────────────────────────────────────

    [TestCase("/", "/a/x.bin", false, ExpectedResult = "a")]
    [TestCase("", "/a/x.bin", false, ExpectedResult = "a")]
    [TestCase("disk:/", "disk:/a/b/y.bin", false, ExpectedResult = "a")]
    [TestCase("/docs", "/docs/a/b/c/y.bin", false, ExpectedResult = "a")]
    [TestCase("/docs/", "/docs/a/y.bin", false, ExpectedResult = "a")]
    [TestCase("/docs", "/docs/top.txt", false, ExpectedResult = null)]
    [TestCase("/docs", "/docs/a", true, ExpectedResult = "a")]
    [TestCase("/docs", "/docs/a/", false, ExpectedResult = "a")]
    [TestCase("/docs", "/docs", true, ExpectedResult = null)]
    [TestCase("/docs", "/docs/", true, ExpectedResult = null)]
    [TestCase("/docs", "/docsold/a/x.bin", false, ExpectedResult = null)]
    [TestCase("/docs", "/other/a/x.bin", false, ExpectedResult = null)]
    [TestCase("/docs", "/docs//x.bin", false, ExpectedResult = null)]
    public string? SubfolderOf_FindsFirstSegmentBelowFolder(string folder, string path, bool isFolder) =>
        FolderSizeTally.SubfolderOf(folder, path, isFolder);

    [Test]
    public void SubfolderOf_ComparisonAndSeparator()
    {
        Assert.That(FolderSizeTally.SubfolderOf("/Docs", "/docs/A/x", comparison: StringComparison.OrdinalIgnoreCase),
            Is.EqualTo("A"));
        Assert.That(FolderSizeTally.SubfolderOf("/Docs", "/docs/A/x"), Is.Null);
        Assert.That(FolderSizeTally.SubfolderOf(@"C:\Docs", @"C:\Docs\a\x.bin", separator: '\\'), Is.EqualTo("a"));
    }

    // ── FolderSizeWalker ────────────────────────────────────────────────────

    private static MemoryFolder SampleTree()
    {
        var root = new MemoryFolder("root");
        var a = root.Folder("a");
        a.File("x.bin", 3);
        var b = a.Folder("b");
        b.File("y.bin", 5);
        b.Folder("deep").File("z.bin", 7);
        root.Folder("c");
        root.File("top.txt", 100);
        return root;
    }

    [Test]
    public async Task Walker_AddsUpEveryDepth_KeepsEmptyFolder_SkipsDirectFiles()
    {
        var sizes = await new MemoryProvider(SampleTree()).MeasureAsync();

        Assert.That(sizes[^1].IsComplete, Is.True);
        Assert.That(sizes.Take(sizes.Count - 1), Is.All.Property(nameof(FolderSizes.IsComplete)).False);
        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 15, ["c"] = 0 }));
    }

    [Test]
    public async Task Walker_Subfolder_MeasuresItsOwnSubfolders()
    {
        var a = (MemoryFolder)SampleTree().Children.Single(c => c.Name == "a");

        var sizes = await new MemoryProvider(a).MeasureAsync();

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["b"] = 12 }));
    }

    [Test]
    public async Task Walker_NoSubfolders_OneCompleteSnapshot()
    {
        var root = new MemoryFolder("root");
        root.File("top.txt", 1);

        var sizes = await new MemoryProvider(root).MeasureAsync();

        Assert.That(sizes, Has.Count.EqualTo(1));
        Assert.That(sizes[0].IsComplete, Is.True);
        Assert.That(sizes[0].Bytes, Is.Empty);
    }

    [Test]
    public async Task Walker_FolderWhoseListingFails_IsSkipped()
    {
        var root = SampleTree();
        var a = (MemoryFolder)root.Children.Single(c => c.Name == "a");
        var b = (MemoryFolder)a.Children.Single(c => c.Name == "b");
        b.Listing = _ => throw new UnauthorizedAccessException();
        var locked = root.Folder("locked");
        locked.File("secret.bin", 50);
        locked.Listing = _ => throw new IOException();

        var sizes = await new MemoryProvider(root).MeasureAsync();

        Assert.That(sizes[^1].IsComplete, Is.True);
        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 3, ["c"] = 0, ["locked"] = 0 }));
    }

    [Test]
    public void Walker_OwnListingFails_Throws()
    {
        var root = SampleTree();
        root.Listing = _ => throw new IOException("gone");

        Assert.ThrowsAsync<IOException>(() => new MemoryProvider(root).MeasureAsync());
    }

    [Test]
    public void Walker_Cancelled_ThrowsOperationCanceled()
    {
        FolderSizeWalker.ReportInterval = TimeSpan.FromMilliseconds(5);
        var root = SampleTree();
        var a = (MemoryFolder)root.Children.Single(c => c.Name == "a");
        var started = new TaskCompletionSource();
        a.Listing = async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        using var cts = new CancellationTokenSource();

        var measure = new MemoryProvider(root).MeasureAsync(ct: cts.Token);
        Assert.That(started.Task.Wait(TimeSpan.FromSeconds(10)), Is.True);
        cts.Cancel();

        Assert.That(async () => await measure, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void Walker_CancelledBeforeStart_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(async () => await new MemoryProvider(SampleTree()).MeasureAsync(ct: cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Walker_Parallel_GivesSameTotals()
    {
        var root = new MemoryFolder("root");
        var expected = new Dictionary<string, long>();

        for (var i = 0; i < 6; i++)
        {
            var top = root.Folder($"f{i}");
            long total = 0;

            for (var j = 0; j < 5; j++)
            {
                var sub = top.Folder($"s{j}");
                sub.File("a.bin", i + j);
                sub.Folder("deeper").File("b.bin", 10);
                total += i + j + 10;
            }

            top.File("own.bin", 1);
            expected[$"f{i}"] = total + 1;
        }

        foreach (var folder in root.Descendants())
            folder.Listing = _ => Task.Delay(1);

        var serial = await new MemoryProvider(root).MeasureAsync(maxParallelism: 1);
        var parallel = await new MemoryProvider(root).MeasureAsync(maxParallelism: 4);

        Assert.That(serial[^1].Bytes, Is.EquivalentTo(expected));
        Assert.That(parallel[^1].Bytes, Is.EquivalentTo(expected));
        Assert.That(parallel[^1].IsComplete, Is.True);
    }

    [Test]
    public async Task Walker_ReportsProgressSnapshotsWhileWalking()
    {
        FolderSizeWalker.ReportInterval = TimeSpan.FromMilliseconds(1);
        var root = SampleTree();

        foreach (var folder in root.Descendants())
            folder.Listing = _ => Task.Delay(30);

        var sizes = await new MemoryProvider(root).MeasureAsync();

        Assert.That(sizes, Has.Count.GreaterThan(1));
        Assert.That(sizes[0].IsComplete, Is.False);
        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 15, ["c"] = 0 }));
    }

    [Test]
    public void Walker_ParallelismBelowOne_Throws()
    {
        Assert.That(async () => await new MemoryProvider(SampleTree()).MeasureAsync(maxParallelism: 0),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }
}

file static class WalkerExtensions
{
    public static async Task<List<FolderSizes>> MeasureAsync(this IFileProvider provider, int maxParallelism = 1,
        CancellationToken ct = default)
    {
        var sizes = new List<FolderSizes>();

        await foreach (var snapshot in FolderSizeWalker.MeasureAsync(provider, maxParallelism, ct: ct))
            sizes.Add(snapshot);

        return sizes;
    }
}

internal sealed class MemoryFile(string name, string fullName, long size) : IFileItem
{
    public string Name => name;
    public string FullName => fullName;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => default;
    long? IFileProviderItem.Size => size;
    public long Size => size;
    public string Extension => Path.GetExtension(name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class MemoryFolder(string name, string fullName = "") : IFolderItem
{
    public List<IFileProviderItem> Children { get; } = [];

    /// <summary>Runs before the listing: throw to fail it, wait to slow it.</summary>
    public Func<CancellationToken, Task>? Listing { get; set; }

    public string Name => name;
    public string FullName => fullName.Length == 0 ? "/" : fullName;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => default;
    public long? Size => null;

    public IFileProvider CreateProvider() => new MemoryProvider(this);

    public MemoryFolder Folder(string childName)
    {
        var folder = new MemoryFolder(childName, $"{fullName}/{childName}");
        Children.Add(folder);
        return folder;
    }

    public void File(string childName, long size) => Children.Add(new MemoryFile(childName, $"{fullName}/{childName}", size));

    public IEnumerable<MemoryFolder> Descendants() =>
        Children.OfType<MemoryFolder>().SelectMany(f => f.Descendants().Prepend(f));
}

internal sealed class MemoryProvider(MemoryFolder folder) : IFileProvider
{
    public string Name => folder.Name;
    public string FullName => folder.FullName;

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (folder.Listing is { } listing)
            await listing(ct);

        foreach (var child in folder.Children.OrderBy(c => c is IFolderItem ? 0 : 1).ToList())
        {
            ct.ThrowIfCancellationRequested();
            yield return child;
        }
    }

    public Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(IReadOnlyList<IFileProviderItem> items,
        bool toRecycleBin, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<FileOperationResult<IFileItem>> MoveFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite,
        CancellationToken ct = default) => throw new NotSupportedException();
}
