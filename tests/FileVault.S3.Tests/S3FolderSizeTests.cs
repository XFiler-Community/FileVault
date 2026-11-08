using FileVault.Core;

namespace FileVault.S3.Tests;

[TestFixture]
public class S3FolderSizeTests
{
    private const string Bucket = "bucket";

    private FakeS3 _s3 = null!;
    private S3Connection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _s3 = new FakeS3();
        _connection = _s3.Connect(Bucket);
        _s3.Put(Bucket, "a/x.bin", new byte[3]);
        _s3.Put(Bucket, "a/b/y.bin", new byte[5]);
        _s3.Put(Bucket, "c/", []);
        _s3.Put(Bucket, "top.txt", new byte[100]);
    }

    [TearDown]
    public void TearDown() => _s3.Dispose();

    private static async Task<List<FolderSizes>> MeasureAsync(IFolderSizeProvider provider) =>
        await provider.MeasureFoldersAsync().ToListAsync();

    [Test]
    public async Task Root_AddsUpEveryKeyBelow_EmptyFolderMarkerIsZero()
    {
        var sizes = await MeasureAsync(new S3FileProvider(_connection));

        Assert.That(sizes[^1].IsComplete, Is.True);
        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 8, ["c"] = 0 }),
            "top.txt lies directly in the folder and is no subfolder's");
        var listings = _s3.Requests.Where(r => r.Contains("list-type=2")).ToList();
        Assert.That(listings, Has.Count.EqualTo(1));
        Assert.That(listings, Has.All.Not.Contains("delimiter"));
    }

    [Test]
    public async Task Subfolder_MeasuresItsOwnSubfolders()
    {
        var sizes = await MeasureAsync(new S3FileProvider(_connection, "/a/"));

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["b"] = 5 }));
    }

    [Test]
    public async Task ManyPages_AreAllCounted()
    {
        _s3.PageSize = 2;

        var sizes = await MeasureAsync(new S3FileProvider(_connection));

        Assert.That(sizes[^1].Bytes, Is.EquivalentTo(new Dictionary<string, long> { ["a"] = 8, ["c"] = 0 }));
    }
}
