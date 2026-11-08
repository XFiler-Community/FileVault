using FileVault.Core;

namespace FileVault.Dropbox.Tests;

[TestFixture]
public class DropboxFolderSizeTests
{
    private FakeDropbox _dropbox = null!;
    private DropboxClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _dropbox = new FakeDropbox();
        _client = _dropbox.CreateClient(new DropboxClientOptions
        {
            JobPollInterval = TimeSpan.Zero,
            DefaultRetryAfter = TimeSpan.Zero
        });
        _dropbox.AddFile("/A/x.bin", new byte[3]);
        _dropbox.AddFile("/A/b/y.bin", new byte[5]);
        _dropbox.AddFolder("/c");
        _dropbox.AddFile("/top.txt", new byte[100]);
    }

    [TearDown]
    public void TearDown() => _dropbox.Dispose();

    private static async Task<List<FolderSizes>> MeasureAsync(IFolderSizeProvider provider) =>
        await provider.MeasureFoldersAsync().ToListAsync();

    [Test]
    public async Task Root_AddsUpEveryDepth_InOneRecursiveListing()
    {
        var sizes = await MeasureAsync(_client.CreateProvider());

        Assert.That(sizes[^1].IsComplete, Is.True);
        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["A"] = 8, ["c"] = 0 }),
            "top.txt lies directly in the folder and is no subfolder's");
        Assert.That(_dropbox.Routes.Where(r => r.StartsWith("files/")), Is.EqualTo(new[] { "files/list_folder" }));
    }

    [Test]
    public async Task Subfolder_MeasuresItsOwnSubfolders_WhateverTheCasing()
    {
        var sizes = await MeasureAsync(_client.CreateProvider("/a"));

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["b"] = 5 }));
        Assert.That(sizes[^1].Find("B"), Is.EqualTo(5), "Dropbox names are case-insensitive");
    }

    [Test]
    public async Task ManyPages_AreAllCounted()
    {
        _dropbox.ListPageSize = 2;

        var sizes = await MeasureAsync(_client.CreateProvider());

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["A"] = 8, ["c"] = 0 }));
        Assert.That(_dropbox.Routes.Count(r => r == "files/list_folder/continue"), Is.GreaterThan(0));
    }
}
