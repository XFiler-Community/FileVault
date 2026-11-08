using FileVault.Core;

namespace FileVault.GitHub.Tests;

[TestFixture]
public class GitHubFolderSizeTests
{
    private FakeGitHub _github = null!;
    private GitHubRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _github = new FakeGitHub();
        _repository = _github.CreateRepository();
        // Git keeps no empty folders: an "empty" one holds an empty .gitkeep
        _github.Commit(new Dictionary<string, byte[]?>
        {
            ["a/x.bin"] = new byte[3],
            ["a/b/y.bin"] = new byte[5],
            ["c/.gitkeep"] = [],
            ["top.txt"] = new byte[100]
        });
    }

    [TearDown]
    public void TearDown() => _github.Dispose();

    private static async Task<List<FolderSizes>> MeasureAsync(IFolderSizeProvider provider) =>
        await provider.MeasureFoldersAsync().ToListAsync();

    [Test]
    public async Task Root_AddsUpTheRecursiveTree()
    {
        var sizes = await MeasureAsync(_repository.CreateProvider());

        Assert.That(sizes, Has.Count.EqualTo(1));
        Assert.That(sizes[0].IsComplete, Is.True);
        Assert.That(sizes[0].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 8, ["c"] = 0 }),
            "top.txt lies directly in the folder and is no subfolder's");
    }

    [Test]
    public async Task Subfolder_MeasuresItsOwnSubfolders()
    {
        var a = (IFolderItem)(await _repository.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync())
            .Single(i => i.Name == "a");

        var sizes = await MeasureAsync((IFolderSizeProvider)a.CreateProvider());

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["b"] = 5 }));
    }

    [Test]
    public async Task TruncatedTree_IsWalkedFolderByFolder()
    {
        _github.RecursiveTreeLimit = 2;

        var sizes = await MeasureAsync(_repository.CreateProvider());

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 8, ["c"] = 0 }));
    }
}
