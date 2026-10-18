using FileVault.Core;
using FileVault.Local;

namespace FileVault.Local.Tests;

[TestFixture]
public class LocalFileProviderCrossProviderTests
{
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
    public async Task CopyFolderInAsync_NonLocalSource_ReturnsNullForOrchestrator()
    {
        var provider = new LocalFileProvider(_rootDir);
        var source = new FakeCloudFolderItem("Photos");
        var dest = await provider.ResolveDestinationFolderAsync(source, overwrite: false);

        var result = await provider.CopyFolderInAsync(source, dest.FullName, new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result, Is.Null);
        Assert.That(Directory.Exists(dest.FullName), Is.False);
    }

    [Test]
    public async Task TryMoveFolderAsync_NonLocalSource_ReturnsNullForOrchestrator()
    {
        var provider = new LocalFileProvider(_rootDir);
        var source = new FakeCloudFolderItem("Photos");
        var dest = await provider.ResolveDestinationFolderAsync(source, overwrite: false);

        var result = await provider.TryMoveFolderAsync(source, dest.FullName, new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result, Is.Null);
        Assert.That(Directory.Exists(dest.FullName), Is.False);
    }

    [Test]
    public async Task CopyFolderInAsync_LocalSource_CopiesRecursively()
    {
        var sourceDir = Path.Combine(_rootDir, "src");
        Directory.CreateDirectory(Path.Combine(sourceDir, "nested"));
        File.WriteAllBytes(Path.Combine(sourceDir, "nested", "a.bin"), [1, 2]);
        var destRoot = Path.Combine(_rootDir, "dst");
        Directory.CreateDirectory(destRoot);

        var source = (await new LocalFileProvider(_rootDir).GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync())
            .OfType<IFolderItem>().Single(f => f.Name == "src");
        var provider = new LocalFileProvider(destRoot);
        var dest = await provider.ResolveDestinationFolderAsync(source, overwrite: false);

        var result = await provider.CopyFolderInAsync(source, dest.FullName, new Progress<double>());

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result, Is.Not.Null);
        Assert.That(File.ReadAllBytes(Path.Combine(destRoot, "src", "nested", "a.bin")), Is.EqualTo(new byte[] { 1, 2 }));
    }

    private sealed class FakeCloudFolderItem(string name) : IFolderItem
    {
        public string Name => name;
        public string FullName => $"gdrive:/root/{name}";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        public long? Size => null;
        public IFileProvider CreateProvider() => throw new NotSupportedException();
    }
}
