using FileVault.Core;

namespace FileVault.Core.Tests;

file sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

[TestFixture]
public class FileOperationsHelperTests
{
    [Test]
    public async Task CopyAsync_CopiesAllBytes()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        using var src = new MemoryStream(data);
        using var dst = new MemoryStream();

        await FileOperationsHelper.CopyAsync(src, data.Length, dst);

        Assert.That(dst.ToArray(), Is.EqualTo(data));
    }

    [Test]
    public async Task CopyAsync_ReportsProgress()
    {
        byte[] data = new byte[3 * 1024 * 1024]; // 3 MB — spans multiple 1 MB buffers
        Random.Shared.NextBytes(data);

        using var src = new MemoryStream(data);
        using var dst = new MemoryStream();
        var reports = new List<double>();

        await FileOperationsHelper.CopyAsync(src, data.Length, dst, new SyncProgress<double>(v => reports.Add(v)));

        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports.Last(), Is.EqualTo(1.0));
        Assert.That(dst.ToArray(), Is.EqualTo(data));
    }

    [Test]
    public async Task CopyAsync_UnknownLength_ReportsOnlyFinal()
    {
        byte[] data = [10, 20, 30];
        using var src = new MemoryStream(data);
        using var dst = new MemoryStream();
        var reports = new List<double>();

        await FileOperationsHelper.CopyAsync(src, totalBytes: 0, dst, new SyncProgress<double>(v => reports.Add(v)));

        Assert.That(reports, Is.EqualTo(new[] { 1.0 }));
    }

    [Test]
    public async Task CopyAsync_EmptyStream_ReportsFinal()
    {
        using var src = new MemoryStream();
        using var dst = new MemoryStream();
        var reports = new List<double>();

        await FileOperationsHelper.CopyAsync(src, totalBytes: 0, dst, new SyncProgress<double>(v => reports.Add(v)));

        Assert.That(dst.Length, Is.EqualTo(0));
        Assert.That(reports, Is.EqualTo(new[] { 1.0 }));
    }
}
