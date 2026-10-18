using FileVault.Core;
using FileVault.Local;

namespace FileVault.Local.Tests;

[TestFixture]
public class LocalFileProviderRecycleBinTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public async Task DeleteAsync_ToRecycleBin_WithoutRecycleBin_FailsAndKeepsItems()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "keep.txt"), [1]);
        Directory.CreateDirectory(Path.Combine(_tempDir, "KeepFolder"));
        var provider = new LocalFileProvider(_tempDir);
        var items = await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await provider.DeleteAsync(items, toRecycleBin: true);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.TypeOf<NotSupportedException>());
        Assert.That(File.Exists(Path.Combine(_tempDir, "keep.txt")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(_tempDir, "KeepFolder")), Is.True);
    }

    [Test]
    public async Task DeleteAsync_ToRecycleBin_UsesRecycleBin()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "file.txt"), [1]);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Folder"));
        var recycleBin = new FakeRecycleBin();
        var provider = new LocalFileProvider(_tempDir, recycleBin);
        var items = await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await provider.DeleteAsync(items, toRecycleBin: true);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(recycleBin.Recycled, Is.EquivalentTo(new[]
        {
            (Path.Combine(_tempDir, "file.txt"), false),
            (Path.Combine(_tempDir, "Folder"), true)
        }));
    }

    [Test]
    public async Task DeleteAsync_ToRecycleBin_RecycleBinThrows_ReturnsFailure()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "file.txt"), [1]);
        var provider = new LocalFileProvider(_tempDir, new FakeRecycleBin(fail: true));
        var items = await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await provider.DeleteAsync(items, toRecycleBin: true);

        Assert.That(result.Exception, Is.TypeOf<IOException>());
        Assert.That(File.Exists(Path.Combine(_tempDir, "file.txt")), Is.True);
    }

    [Test]
    public async Task DeleteAsync_Permanently_DoesNotUseRecycleBin()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "file.txt"), [1]);
        var recycleBin = new FakeRecycleBin();
        var provider = new LocalFileProvider(_tempDir, recycleBin);
        var items = await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await provider.DeleteAsync(items, toRecycleBin: false);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(recycleBin.Recycled, Is.Empty);
        Assert.That(File.Exists(Path.Combine(_tempDir, "file.txt")), Is.False);
    }

    [Test]
    public async Task CreateProvider_OfChildFolder_KeepsRecycleBin()
    {
        var childDir = Path.Combine(_tempDir, "Child");
        Directory.CreateDirectory(childDir);
        File.WriteAllBytes(Path.Combine(childDir, "nested.txt"), [1]);
        var recycleBin = new FakeRecycleBin();
        var provider = new LocalFileProvider(_tempDir, recycleBin);
        var folder = (await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync())
            .OfType<IFolderItem>().Single();

        var childProvider = folder.CreateProvider();
        var childItems = await childProvider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var result = await childProvider.DeleteAsync(childItems, toRecycleBin: true);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(recycleBin.Recycled, Is.EqualTo(new[] { (Path.Combine(childDir, "nested.txt"), false) }));
    }

    private sealed class FakeRecycleBin(bool fail = false) : ILocalRecycleBin
    {
        public List<(string Path, bool IsDirectory)> Recycled { get; } = [];

        public void MoveToRecycleBin(string path, bool isDirectory)
        {
            if (fail)
                throw new IOException("Trash is unavailable.");
            Recycled.Add((path, isDirectory));
        }
    }
}
