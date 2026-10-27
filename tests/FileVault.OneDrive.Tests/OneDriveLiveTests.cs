using FileVault.Core;

namespace FileVault.OneDrive.Tests;

/// <summary>
/// Reads the root of a real OneDrive to check that the fake speaks the same Graph. Opt in by setting
/// ONEDRIVE_ACCESS_TOKEN to a Graph token with Files.Read (it only reads: lists the root, quota, one small file).
/// </summary>
[TestFixture]
[Category("Integration")]
public class OneDriveLiveTests
{
    private OneDriveClient _client = null!;

    [OneTimeSetUp]
    public void Connect()
    {
        if (Environment.GetEnvironmentVariable("ONEDRIVE_ACCESS_TOKEN") is not { Length: > 0 } token)
        {
            Assert.Ignore("Set ONEDRIVE_ACCESS_TOKEN to read a real OneDrive.");
            return;
        }

        _client = new OneDriveClient(new HttpClient(), (_, _) => ValueTask.FromResult(token));
    }

    [Test]
    public async Task ListsRootReadsQuotaAndAFile()
    {
        var drive = await _client.GetDriveAsync();
        var items = await drive.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        TestContext.Out.WriteLine($"{drive.DriveType}: {drive.TotalFreeSpace}/{drive.TotalSize}; "
                                  + string.Join(", ", items.Select(i => i.Name)));
        Assert.That(drive.TotalSize, Is.GreaterThan(0));

        if (items.OfType<IFileItem>().FirstOrDefault(f => f.Size is > 0 and < 1_000_000) is { } file)
        {
            var (stream, length) = await file.OpenReadAsync();
            await using (stream) { }
            Assert.That(length, Is.EqualTo(file.Size));
        }
    }
}
