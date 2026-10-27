using FileVault.Core;

namespace FileVault.Dropbox.Tests;

/// <summary>
/// Read-only checks against a real Dropbox account, to see that the fake speaks the same API. Opt in with
/// DROPBOX_ACCESS_TOKEN (a short-lived token from the App Console, scopes files.metadata.read and
/// files.content.read).
/// </summary>
[TestFixture]
[Category("Integration")]
public class DropboxLiveTests
{
    private DropboxClient _client = null!;

    [OneTimeSetUp]
    public void Connect()
    {
        var token = Environment.GetEnvironmentVariable("DROPBOX_ACCESS_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
            Assert.Ignore("Set DROPBOX_ACCESS_TOKEN to read a real Dropbox account.");

        _client = new DropboxClient(_ => ValueTask.FromResult(token), new HttpClient());
    }

    [Test]
    public async Task ListsRootReadsQuotaAndAFile()
    {
        var drive = await _client.GetDriveAsync();
        var items = await drive.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();

        TestContext.Out.WriteLine($"{drive.TotalSize - drive.TotalFreeSpace} of {drive.TotalSize}: " +
                                  string.Join(", ", items.Select(i => i.FullName)));
        Assert.That(drive.TotalSize, Is.GreaterThan(0));

        if (items.OfType<IFileItem>().FirstOrDefault(f => f.Size is > 0 and < 1_000_000) is { } file)
        {
            var (stream, length) = await file.OpenReadAsync();
            await using (stream) { }
            Assert.That(length, Is.EqualTo(file.Size));
        }
    }
}
