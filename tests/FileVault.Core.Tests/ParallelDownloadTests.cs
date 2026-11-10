using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace FileVault.Core.Tests;

[TestFixture]
public class ParallelDownloadTests
{
    private static readonly ParallelDownloadOptions Small = new()
    {
        Parallelism = 3,
        ChunkSize = 1000,
        MinimumLength = 2000
    };

    private static byte[] Data(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7 + i / 251)).ToArray();

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            var result = new MemoryStream();
            await stream.CopyToAsync(result);
            return result.ToArray();
        }
    }

    private static Task<Stream> Slice(byte[] data, long offset, int count) =>
        Task.FromResult<Stream>(new MemoryStream(data, (int)offset, count, writable: false));

    [Test]
    public async Task LargeFile_ComesWhole_InOrder_FromSeveralRanges()
    {
        var data = Data(10_500);
        var ranges = new ConcurrentBag<(long Offset, int Count)>();

        var stream = await ParallelDownload.OpenAsync(data.Length,
            async (offset, count, _) =>
            {
                ranges.Add((offset, count));
                await Task.Delay(Random.Shared.Next(1, 15));
                return await Slice(data, offset, count);
            },
            _ => throw new AssertionException("the whole file must not be asked for"),
            Small);

        Assert.That(await ReadAllAsync(stream), Is.EqualTo(data));
        Assert.That(ranges.OrderBy(r => r.Offset).Select(r => r.Offset),
            Is.EqualTo(Enumerable.Range(0, 11).Select(i => i * 1000L)));
        Assert.That(ranges.Single(r => r.Offset == 10_000).Count, Is.EqualTo(500));
    }

    [Test]
    public async Task Checked_LargeFile_HandsRangesOutAsTheyArrive()
    {
        var data = Data(10_500);

        var stream = await ParallelDownload.OpenCheckedAsync(data.Length,
            (offset, count, _) => Slice(data, offset, count),
            _ => throw new AssertionException("the whole file must not be asked for"),
            Small);

        Assert.That(stream.CanSeek, Is.False, "not spooled to a temporary file first");
        Assert.That(await ReadAllAsync(stream), Is.EqualTo(data));
    }

    [Test]
    public async Task Checked_WholeFile_IsBufferedFirst_SoABrokenBodyThrowsHere()
    {
        var data = Data(500);

        var stream = await ParallelDownload.OpenCheckedAsync(data.Length,
            (_, _, _) => throw new AssertionException("a small file is not read in ranges"),
            _ => Task.FromResult<Stream>(new MemoryStream(data)),
            Small);

        Assert.That(stream.CanSeek, Is.True);
        Assert.That(await ReadAllAsync(stream), Is.EqualTo(data));

        Assert.That(async () => await ParallelDownload.OpenCheckedAsync(data.Length,
                (_, _, _) => throw new AssertionException("a small file is not read in ranges"),
                _ => Task.FromResult<Stream>(new BreakingStream(data[..100])),
                Small),
            Throws.InstanceOf<IOException>());
    }

    private sealed class BreakingStream(byte[] head) : MemoryStream(head)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, count) is > 0 and var read ? read : throw new IOException("connection reset");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            await base.ReadAsync(buffer, ct) is > 0 and var read ? read : throw new IOException("connection reset");
    }

    [Test]
    public async Task ReadsAhead_NoMoreThanParallelismRangesAtOnce()
    {
        var data = Data(20_000);
        var inFlight = 0;
        var peak = 0;

        var stream = await ParallelDownload.OpenAsync(data.Length,
            async (offset, count, _) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref peak, now);
                await Task.Delay(5);
                Interlocked.Decrement(ref inFlight);
                return await Slice(data, offset, count);
            },
            _ => throw new AssertionException("the whole file must not be asked for"),
            Small);

        Assert.That(await ReadAllAsync(stream), Is.EqualTo(data));
        Assert.That(peak, Is.InRange(2, Small.Parallelism));
    }

    [Test]
    public async Task SmallFile_GoesAsOnePlainRequest()
    {
        var data = Data(1500);

        var stream = await ParallelDownload.OpenAsync(data.Length,
            (_, _, _) => throw new AssertionException("no ranges for a small file"),
            _ => Task.FromResult<Stream>(new MemoryStream(data)),
            Small);

        Assert.That(await ReadAllAsync(stream), Is.EqualTo(data));
    }

    [Test]
    public async Task ServerWithoutRanges_FallsBackToOnePlainRequest()
    {
        var data = Data(5000);
        var whole = 0;

        var stream = await ParallelDownload.OpenAsync(data.Length,
            (_, _, _) => throw new RangeNotSupportedException("200"),
            _ =>
            {
                whole++;
                return Task.FromResult<Stream>(new MemoryStream(data));
            },
            Small);

        Assert.That(await ReadAllAsync(stream), Is.EqualTo(data));
        Assert.That(whole, Is.EqualTo(1));
    }

    [Test]
    public async Task ShortRange_IsRetried_NotHandedOutShort()
    {
        var data = Data(5000);
        var attempts = new ConcurrentDictionary<long, int>();

        var stream = await ParallelDownload.OpenAsync(data.Length,
            (offset, count, _) =>
            {
                var attempt = attempts.AddOrUpdate(offset, 1, (_, n) => n + 1);
                return offset == 2000 && attempt == 1 ? Slice(data, offset, count - 10) : Slice(data, offset, count);
            },
            _ => throw new AssertionException("the whole file must not be asked for"),
            Small);

        Assert.That(await ReadAllAsync(stream), Is.EqualTo(data));
        Assert.That(attempts[2000], Is.EqualTo(2));
    }

    [Test]
    public void RangeThatKeepsFailing_SurfacesFromRead()
    {
        var data = Data(5000);

        Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            var stream = await ParallelDownload.OpenAsync(data.Length,
                (offset, count, _) => offset == 3000
                    ? throw new HttpRequestException("connection reset")
                    : Slice(data, offset, count),
                _ => throw new AssertionException("the whole file must not be asked for"),
                Small);

            await ReadAllAsync(stream);
        });
    }

    [Test]
    public void EnsurePartialContent_200_MeansNoRanges()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };

        Assert.Throws<RangeNotSupportedException>(() => ParallelDownload.EnsurePartialContent(response, 0, 10, 100));
    }

    [Test]
    public void EnsurePartialContent_FileOfAnotherLength_MeansNoRanges()
    {
        using var response = Partial(0, 9, 120);

        Assert.Throws<RangeNotSupportedException>(() => ParallelDownload.EnsurePartialContent(response, 0, 10, 100));
    }

    [Test]
    public void EnsurePartialContent_AnotherRange_IsBroken()
    {
        using var response = Partial(10, 19, 100);

        Assert.Throws<IOException>(() => ParallelDownload.EnsurePartialContent(response, 0, 10, 100));
    }

    [Test]
    public void EnsurePartialContent_TheRangeAskedFor_Passes()
    {
        using var response = Partial(20, 29, 100);

        Assert.DoesNotThrow(() => ParallelDownload.EnsurePartialContent(response, 20, 10, 100));
    }

    private static HttpResponseMessage Partial(long from, long to, long length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, length);
        return response;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;

        while (value > (current = Volatile.Read(ref target)))
            Interlocked.CompareExchange(ref target, value, current);
    }
}
