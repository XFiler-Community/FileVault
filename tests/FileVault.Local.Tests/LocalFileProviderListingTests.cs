using FileVault.Core;
using FileVault.Local;

namespace FileVault.Local.Tests;

[TestFixture]
public class LocalFileProviderListingTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "fv-listing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task ListsFoldersFirstAcrossSeveralBatches()
    {
        // More entries than one batch, folders created last so the directory order does not put them first.
        for (var i = 0; i < 600; i++)
            await File.WriteAllBytesAsync(Path.Combine(_root, $"file{i:D3}.bin"), new byte[i % 7]);
        for (var i = 0; i < 5; i++)
            Directory.CreateDirectory(Path.Combine(_root, $"dir{i}"));

        var items = await new LocalFileProvider(_root).GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        Assert.That(items, Has.Count.EqualTo(605));
        Assert.That(items.Take(5), Is.All.InstanceOf<SystemFolderItem>());
        Assert.That(items.Skip(5), Is.All.InstanceOf<SystemFileItem>());
        Assert.That(items.Select(i => i.Name).Distinct().Count(), Is.EqualTo(605));
    }

    [Test]
    public async Task ItemsCarryTheEntrysMetadata()
    {
        var path = Path.Combine(_root, "a.txt");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var written = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Local);
        File.SetLastWriteTime(path, written);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var items = await new LocalFileProvider(_root).GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var folder = (SystemFolderItem)items[0];
        Assert.That(folder.Name, Is.EqualTo("sub"));
        Assert.That(folder.FullName, Is.EqualTo(Path.Combine(_root, "sub")));
        var file = (SystemFileItem)items[1];
        Assert.That(file.FullName, Is.EqualTo(path));
        Assert.That(file.Size, Is.EqualTo(3));
        Assert.That(file.ChangedDate, Is.EqualTo(new DateTimeOffset(written)));
    }

    [Test]
    public async Task HiddenItemsAreSkippedUnlessAsked()
    {
        File.WriteAllText(Path.Combine(_root, "shown.txt"), "");
        var hidden = Path.Combine(_root, OperatingSystem.IsWindows() ? "hidden.txt" : ".hidden.txt");
        File.WriteAllText(hidden, "");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(hidden, FileAttributes.Hidden);

        var provider = new LocalFileProvider(_root);
        var shown = await provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync();
        var all = await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        Assert.That(shown.Select(i => i.Name), Is.EqualTo(new[] { "shown.txt" }));
        Assert.That(all, Has.Count.EqualTo(2));
        Assert.That(all.Single(i => i.FullName == hidden).IsHidden, Is.True);
    }

    [Test]
    public void ACancelledListingThrows()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(async () => await new LocalFileProvider(_root).GetItemsAsync(FileProviderFilter.ShowAll, cts.Token)
            .ToListAsync(), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void CancellingMidListingStopsIt()
    {
        for (var i = 0; i < 600; i++)
            File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "");
        using var cts = new CancellationTokenSource();
        var seen = 0;

        Assert.That(async () =>
        {
            await foreach (var _ in new LocalFileProvider(_root).GetItemsAsync(FileProviderFilter.ShowAll, cts.Token))
            {
                if (++seen == 1)
                    await cts.CancelAsync();
            }
        }, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(seen, Is.LessThan(600));
    }

    [Test]
    public void AMissingFolderThrowsDirectoryNotFound()
    {
        var missing = Path.Combine(_root, "missing");

        Assert.That(async () => await new LocalFileProvider(missing).GetItemsAsync(FileProviderFilter.ShowAll)
            .ToListAsync(), Throws.InstanceOf<DirectoryNotFoundException>());
    }

    [Test]
    public async Task RenameItemAsync_InSubfolderOfASearch_RenamesThereAndReturnsTheItem()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(sub, "a.txt"), [5]);
        var item = new SystemFileItem(new FileInfo(Path.Combine(sub, "a.txt")));

        var result = await new LocalFileProvider(_root).RenameItemAsync(item, "b.txt");

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result!.FullName, Is.EqualTo(Path.Combine(sub, "b.txt")));
        Assert.That(result.Result.Size, Is.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(sub, "b.txt")), Is.True);
    }
}
