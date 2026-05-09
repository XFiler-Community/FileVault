using System.Runtime.CompilerServices;
using FileVault.Core;

namespace FileVault.Contract.Tests;

[TestFixture]
public abstract class FileProviderContractTests
{
    protected IFileProvider Provider { get; private set; } = null!;

    protected abstract Task<IFileProvider> CreateProviderAsync();
    protected abstract Task SeedFileAsync(string name, byte[] content);
    protected abstract Task SeedFolderAsync(string name);
    protected abstract Task<byte[]> ReadFileAsync(string name);
    protected abstract Task<bool> FileExistsAsync(string name);
    protected abstract string GetDestPath(string name);

    [SetUp]
    public async Task BaseSetUp() => Provider = await CreateProviderAsync();

    [TearDown]
    public abstract Task TearDown();

    [Test]
    public async Task GetItemsAsync_EmptyDirectory_ReturnsEmpty()
    {
        var items = await Provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync();
        Assert.That(items, Is.Empty);
    }

    [Test]
    public async Task GetItemsAsync_WithFiles_ReturnsItems()
    {
        await SeedFileAsync("a.txt", [1]);
        await SeedFolderAsync("SubDir");

        var items = await Provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        Assert.That(items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task CreateFolderAsync_ReturnsItemAndCreatesDirectory()
    {
        var result = await Provider.CreateFolderAsync("TestFolder");
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Result!.Name, Is.EqualTo("TestFolder"));
        Assert.That(await Provider.ContainsFolderAsync("TestFolder"), Is.True);
    }

    [Test]
    public async Task CreateFolderAsync_NameConflict_AppendsIndex()
    {
        await SeedFolderAsync("Dup");
        var result = await Provider.CreateFolderAsync("Dup");
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Result!.Name, Is.EqualTo("Dup (2)"));
    }

    [Test]
    public async Task CreateEmptyTextFileAsync_CreatesFile()
    {
        var result = await Provider.CreateEmptyTextFileAsync("note.txt");
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Result!.Name, Is.EqualTo("note.txt"));
        Assert.That(await Provider.ContainsFileAsync("note.txt"), Is.True);
    }

    [Test]
    public async Task CopyFileInAsync_CopiesContent()
    {
        byte[] data = [1, 2, 3, 4, 5];
        await SeedFileAsync("source.bin", data);
        var items = await Provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var srcItem = items.OfType<IFileItem>().Single(i => i.Name == "source.bin");

        var result = await Provider.CopyFileInAsync(srcItem, GetDestPath("copy.bin"), new NullProgress());

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(await ReadFileAsync("copy.bin"), Is.EqualTo(data));
        Assert.That(await FileExistsAsync("source.bin"), Is.True, "source must remain");
    }

    [Test]
    public async Task MoveFileInAsync_RemovesSource()
    {
        await SeedFileAsync("move.txt", [42]);
        var items = await Provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var srcItem = items.OfType<IFileItem>().Single();

        var result = await Provider.MoveFileInAsync(srcItem, GetDestPath("moved.txt"), new NullProgress());

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(await FileExistsAsync("move.txt"), Is.False, "source must be deleted");
        Assert.That(await FileExistsAsync("moved.txt"), Is.True);
    }

    [Test]
    public async Task DeleteAsync_RemovesItems()
    {
        await SeedFileAsync("del.txt", []);
        var items = await Provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        var result = await Provider.DeleteAsync(items, toRecycleBin: false);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(await FileExistsAsync("del.txt"), Is.False);
    }

    [Test]
    public async Task RenameAsync_File_ChangesName()
    {
        await SeedFileAsync("old.txt", []);
        var items = await Provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var result = await Provider.RenameAsync(items.Single(), "new.txt");
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(await FileExistsAsync("new.txt"), Is.True);
        Assert.That(await FileExistsAsync("old.txt"), Is.False);
    }

    [Test]
    public async Task ContainsFileAsync_ExistingFile_ReturnsTrue()
    {
        await SeedFileAsync("exists.txt", []);
        Assert.That(await Provider.ContainsFileAsync("exists.txt"), Is.True);
    }

    [Test]
    public async Task ContainsFileAsync_MissingFile_ReturnsFalse()
    {
        Assert.That(await Provider.ContainsFileAsync("ghost.txt"), Is.False);
    }

    [Test]
    public async Task ContainsFolderAsync_ExistingFolder_ReturnsTrue()
    {
        await SeedFolderAsync("existing");
        Assert.That(await Provider.ContainsFolderAsync("existing"), Is.True);
    }

    [Test]
    public async Task ContainsFolderAsync_MissingFolder_ReturnsFalse()
    {
        Assert.That(await Provider.ContainsFolderAsync("ghost"), Is.False);
    }

    [Test]
    public async Task ResolveDestinationFileAsync_NoConflict_KeepsName()
    {
        var fake = new FakeFileItem("report.pdf");
        var dest = await Provider.ResolveDestinationFileAsync(fake, overwrite: false);
        Assert.That(dest.Name, Is.EqualTo("report.pdf"));
    }

    [Test]
    public async Task ResolveDestinationFileAsync_Conflict_AppendsIndex()
    {
        await SeedFileAsync("report.pdf", []);
        var fake = new FakeFileItem("report.pdf");
        var dest = await Provider.ResolveDestinationFileAsync(fake, overwrite: false);
        Assert.That(dest.Name, Is.EqualTo("report (2).pdf"));
    }

    [Test]
    public async Task ResolveDestinationFileAsync_MultipleConflicts_IncrementsIndex()
    {
        await SeedFileAsync("report.pdf", []);
        await SeedFileAsync("report (2).pdf", []);
        var fake = new FakeFileItem("report.pdf");
        var dest = await Provider.ResolveDestinationFileAsync(fake, overwrite: false);
        Assert.That(dest.Name, Is.EqualTo("report (3).pdf"));
    }

    [Test]
    public async Task ResolveDestinationFileAsync_Overwrite_KeepsName()
    {
        await SeedFileAsync("report.pdf", []);
        var fake = new FakeFileItem("report.pdf");
        var dest = await Provider.ResolveDestinationFileAsync(fake, overwrite: true);
        Assert.That(dest.Name, Is.EqualTo("report.pdf"));
    }

    protected sealed class FakeFileItem(string name) : IFileItem
    {
        public string Name { get; } = name;
        public string FullName => name;
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

    protected sealed class NullProgress : IProgress<double>
    {
        public void Report(double value) { }
    }
}
