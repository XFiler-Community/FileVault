using FileVault.Core;
using FileVault.Local;

namespace FileVault.Local.Tests;

[TestFixture]
public class LocalFileProviderNameSafetyTests
{
    private string _parentDir = null!;
    private string _rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        _parentDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _rootDir = Path.Combine(_parentDir, "root");
        Directory.CreateDirectory(_rootDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_parentDir))
            Directory.Delete(_parentDir, recursive: true);
    }

    [TestCase("../escaped.txt")]
    [TestCase("sub/escaped.txt")]
    [TestCase("..")]
    [TestCase(".")]
    public async Task ResolveDestinationFileAsync_NameWithPath_StaysInRoot(string sourceName)
    {
        var provider = new LocalFileProvider(_rootDir);

        var dest = await provider.ResolveDestinationFileAsync(new FakeFileItem(sourceName), overwrite: false);

        Assert.That(Path.GetDirectoryName(dest.FullName), Is.EqualTo(_rootDir));
        Assert.That(dest.Name, Is.Not.EqualTo("..").And.Not.EqualTo("."));
    }

    [Test]
    public async Task ResolveDestinationFileAsync_CloudNameWithSlash_ReplacesSlash()
    {
        var provider = new LocalFileProvider(_rootDir);

        var dest = await provider.ResolveDestinationFileAsync(new FakeFileItem("2024/25.pdf"), overwrite: true);

        Assert.That(dest.FullName, Is.EqualTo(Path.Combine(_rootDir, "2024_25.pdf")));
    }

    [Test]
    public async Task CopyFileInAsync_ToResolvedDestination_WritesInsideRoot()
    {
        var provider = new LocalFileProvider(_rootDir);
        var source = new FakeFileItem("../escaped.txt", [1, 2, 3]);

        var dest = await provider.ResolveDestinationFileAsync(source, overwrite: false);
        var result = await provider.CopyFileInAsync(source, dest.FullName, new Progress<double>());

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(File.Exists(Path.Combine(_parentDir, "escaped.txt")), Is.False);
        Assert.That(File.ReadAllBytes(dest.FullName), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [TestCase("../escaped")]
    [TestCase("..")]
    public async Task ResolveDestinationFolderAsync_NameWithPath_StaysInRoot(string sourceName)
    {
        var provider = new LocalFileProvider(_rootDir);
        var dest = await provider.ResolveDestinationFolderAsync(new FakeFolderItem(sourceName), overwrite: false);

        Assert.That(Path.GetDirectoryName(dest.FullName), Is.EqualTo(_rootDir));
    }

    [TestCase("../escaped")]
    [TestCase("a/b")]
    [TestCase("..")]
    [TestCase("")]
    public async Task CreateFolderAsync_InvalidName_FailsWithoutCreating(string name)
    {
        var provider = new LocalFileProvider(_rootDir);

        var result = await provider.CreateFolderAsync(name);

        Assert.That(result.Exception, Is.TypeOf<ArgumentException>());
        Assert.That(Directory.Exists(Path.Combine(_parentDir, "escaped")), Is.False);
        Assert.That(Directory.EnumerateFileSystemEntries(_rootDir), Is.Empty);
    }

    [TestCase("../escaped.txt")]
    [TestCase("a/b.txt")]
    public async Task CreateEmptyTextFileAsync_InvalidName_FailsWithoutCreating(string name)
    {
        var provider = new LocalFileProvider(_rootDir);

        var result = await provider.CreateEmptyTextFileAsync(name);

        Assert.That(result.Exception, Is.TypeOf<ArgumentException>());
        Assert.That(File.Exists(Path.Combine(_parentDir, "escaped.txt")), Is.False);
    }

    [TestCase("../escaped.txt")]
    [TestCase("..")]
    public async Task RenameAsync_InvalidName_FailsAndKeepsFile(string newName)
    {
        File.WriteAllBytes(Path.Combine(_rootDir, "file.txt"), [1]);
        var provider = new LocalFileProvider(_rootDir);
        var item = (await provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync()).Single();

        var result = await provider.RenameAsync(item, newName);

        Assert.That(result.Exception, Is.TypeOf<ArgumentException>());
        Assert.That(File.Exists(Path.Combine(_rootDir, "file.txt")), Is.True);
        Assert.That(File.Exists(Path.Combine(_parentDir, "escaped.txt")), Is.False);
    }

    private sealed class FakeFileItem(string name, byte[]? content = null) : IFileItem
    {
        public string Name => name;
        public string FullName => $"fake:{name}";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        long? IFileProviderItem.Size => Size;
        public long Size => content?.Length ?? 0;
        public string Extension => Path.GetExtension(name);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
            => Task.FromResult<(Stream, long)>((new MemoryStream(content ?? []), Size));

        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeFolderItem(string name) : IFolderItem
    {
        public string Name => name;
        public string FullName => $"fake:{name}";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        public long? Size => null;
        public IFileProvider CreateProvider() => throw new NotSupportedException();
    }
}
