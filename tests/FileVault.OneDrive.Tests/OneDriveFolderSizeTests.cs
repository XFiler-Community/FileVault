using FileVault.Core;

namespace FileVault.OneDrive.Tests;

[TestFixture]
public class OneDriveFolderSizeTests
{
    private FakeGraph _graph = null!;
    private OneDriveClient _client = null!;
    private string _a = null!;

    [SetUp]
    public void SetUp()
    {
        _graph = new FakeGraph();
        _client = _graph.CreateClient();
        _a = _graph.AddFolder(FakeGraph.RootId, "a");
        _graph.AddFile(_a, "x.bin", new byte[3]);
        var b = _graph.AddFolder(_a, "b");
        _graph.AddFile(b, "y.bin", new byte[5]);
        _graph.AddFolder(FakeGraph.RootId, "c");
        _graph.AddFile(FakeGraph.RootId, "top.txt", new byte[100]);
    }

    [TearDown]
    public void TearDown() => _graph.Dispose();

    private static async Task<List<FolderSizes>> MeasureAsync(IFolderSizeProvider provider) =>
        await provider.MeasureFoldersAsync().ToListAsync();

    [Test]
    public async Task Root_TakesFolderSizesFromOneListing()
    {
        var requestsBefore = _graph.Requests.Count;

        var sizes = await MeasureAsync(_client.CreateProvider());

        Assert.That(sizes, Has.Count.EqualTo(1));
        Assert.That(sizes[0].IsComplete, Is.True);
        Assert.That(sizes[0].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 8, ["c"] = 0 }),
            "top.txt lies directly in the folder and is no subfolder's");
        Assert.That(_graph.Requests.Count - requestsBefore, Is.EqualTo(1));
    }

    [Test]
    public async Task Subfolder_MeasuresItsOwnSubfolders()
    {
        var sizes = await MeasureAsync(_client.CreateProvider(_a, "a"));

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["b"] = 5 }));
    }
}
