using FileVault.Core;

namespace FileVault.Core.Tests;

[TestFixture]
public class TemporaryFileBufferTests
{
    [Test]
    public async Task CreateAsync_ReturnsContentFromStart_AndDeletesFileOnDispose()
    {
        byte[] data = [1, 2, 3, 4, 5];

        var stream = await TemporaryFileBuffer.CreateAsync((destination, ct) => destination.WriteAsync(data, ct).AsTask());
        var path = stream.Name;

        using (var copy = new MemoryStream())
        {
            await stream.CopyToAsync(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(data));
        }

        Assert.That(stream.Length, Is.EqualTo(data.Length));
        await stream.DisposeAsync();
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void CreateAsync_FillFails_DeletesFileAndRethrows()
    {
        string? path = null;

        Assert.ThrowsAsync<IOException>(() => TemporaryFileBuffer.CreateAsync((destination, _) =>
        {
            path = ((FileStream)destination).Name;
            throw new IOException("download failed");
        }));

        Assert.That(path, Is.Not.Null);
        Assert.That(File.Exists(path), Is.False);
    }
}
