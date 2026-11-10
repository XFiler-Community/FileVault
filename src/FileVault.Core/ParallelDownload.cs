using System.Buffers;
using System.Net;

namespace FileVault.Core;

/// <summary>Tuning of <see cref="ParallelDownload"/>.</summary>
public sealed class ParallelDownloadOptions
{
    public static ParallelDownloadOptions Default { get; } = new();

    /// <summary>Range requests in flight at once, 4 by default. 1 turns parallel download off.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>Bytes per range request, 8 MiB by default. Memory held per file is about (Parallelism + 1) × ChunkSize.</summary>
    public int ChunkSize { get; init; } = 8 * 1024 * 1024;

    /// <summary>Files shorter than this go as one plain request, 16 MiB by default.</summary>
    public long MinimumLength { get; init; } = 16 * 1024 * 1024;

    /// <summary>Attempts per range before the download fails, 3 by default.</summary>
    public int AttemptsPerChunk { get; init; } = 3;
}

/// <summary>
/// The server answered a range request with the whole content (or no range at all): it cannot serve ranges, and
/// <see cref="ParallelDownload"/> falls back to one plain request.
/// </summary>
public sealed class RangeNotSupportedException(string message) : NotSupportedException(message);

/// <summary>
/// Downloads a file of a known length as several concurrent range requests and hands it out as one stream read from
/// start to end. A single HTTP stream is often throttled per connection (clouds limit each request, not the account),
/// so a few ranges at once fill the line where one cannot.
/// </summary>
public static class ParallelDownload
{
    /// <summary>
    /// Opens <paramref name="length"/> bytes for sequential reading. Files shorter than
    /// <see cref="ParallelDownloadOptions.MinimumLength"/> and servers that do not serve ranges (the first range throws
    /// <see cref="RangeNotSupportedException"/>) get <paramref name="openWhole"/> instead.
    /// </summary>
    /// <param name="openRange">
    /// Opens <c>count</c> bytes from <c>offset</c>; throws <see cref="RangeNotSupportedException"/> when the server
    /// ignores the range. A body shorter than asked is retried, so it never reaches the reader as a short file.
    /// </param>
    /// <param name="openWhole">Opens the whole content as one plain request.</param>
    public static async Task<Stream> OpenAsync(long length,
        Func<long, int, CancellationToken, Task<Stream>> openRange,
        Func<CancellationToken, Task<Stream>> openWhole,
        ParallelDownloadOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openRange);
        ArgumentNullException.ThrowIfNull(openWhole);

        options ??= ParallelDownloadOptions.Default;

        if (length < options.MinimumLength || options.Parallelism <= 1 || options.ChunkSize <= 0)
            return await openWhole(ct).ConfigureAwait(false);

        var stream = new ParallelRangeStream(length, openRange, options);

        try
        {
            await stream.StartAsync(ct).ConfigureAwait(false);
            return stream;
        }
        catch (RangeNotSupportedException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return await openWhole(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="OpenAsync"/> for a caller that may delete the source after copying it. Ranges are handed out as they
    /// arrive — each is checked for its length, so the stream never ends early. The whole-file request
    /// (<paramref name="openWhole"/>: a small file, or a server without ranges) is downloaded into a temporary file
    /// first, so a body that breaks off surfaces here rather than as a short file. Either way the stream is read
    /// front to back; take its length from the listing, not from the stream.
    /// </summary>
    public static Task<Stream> OpenCheckedAsync(long length,
        Func<long, int, CancellationToken, Task<Stream>> openRange,
        Func<CancellationToken, Task<Stream>> openWhole,
        ParallelDownloadOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openWhole);

        return OpenAsync(length, openRange,
            async token => await TemporaryFileBuffer.CreateAsync(async (destination, t) =>
            {
                var whole = await openWhole(t).ConfigureAwait(false);

                await using (whole.ConfigureAwait(false))
                    await whole.CopyToAsync(destination, t).ConfigureAwait(false);
            }, token).ConfigureAwait(false),
            options, ct);
    }

    /// <summary>
    /// Checks that an HTTP response carries exactly the range asked for, of a file of <paramref name="length"/> bytes.
    /// A 200, or a 206 of a file of another length (it changed since it was listed), throws
    /// <see cref="RangeNotSupportedException"/>, so the first range falls back to one plain request; a 206 of another
    /// range is a broken response (<see cref="IOException"/>). Disposes the response when it throws.
    /// </summary>
    public static void EnsurePartialContent(HttpResponseMessage response, long offset, int count, long length)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose();
            throw new RangeNotSupportedException($"The server answered a range request with {(int)response.StatusCode}.");
        }

        var range = response.Content.Headers.ContentRange;
        EnsureRange(range?.HasRange == true ? range.From : null, range?.HasRange == true ? range.To : null, range?.Length,
            offset, count, length, response);
    }

    /// <summary>
    /// <see cref="EnsurePartialContent"/> for a client that reports the range it got rather than the response:
    /// <paramref name="totalLength"/> other than <paramref name="length"/> means the file changed since it was listed.
    /// </summary>
    public static void EnsureLength(long? totalLength, long length)
    {
        if (totalLength is { } total && total != length)
            throw new RangeNotSupportedException($"The file is {total} bytes now, {length} when it was listed.");
    }

    /// <summary>
    /// The body of <paramref name="response"/> as a stream that disposes the response with it — what an
    /// <c>openRange</c> or <c>openWhole</c> callback returns. Disposes the response when it throws.
    /// </summary>
    public static async Task<Stream> OpenBodyAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            return new ResponseBodyStream(response, await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void EnsureRange(long? from, long? to, long? total, long offset, int count, long length,
        IDisposable response)
    {
        if (total is { } t && t != length)
        {
            response.Dispose();
            throw new RangeNotSupportedException($"The file is {t} bytes now, {length} when it was listed.");
        }

        if (from is not null && (from != offset || to != offset + count - 1))
        {
            response.Dispose();
            throw new IOException($"Asked for bytes {offset}-{offset + count - 1}, the server sent {from}-{to}.");
        }
    }
}

/// <summary>
/// Reads ahead up to <see cref="ParallelDownloadOptions.Parallelism"/> chunks at once and returns them in order.
/// Chunks live in pooled buffers; a failed chunk is retried, and one that keeps failing surfaces from ReadAsync.
/// </summary>
internal sealed class ParallelRangeStream : Stream
{
    private readonly long _length;
    private readonly Func<long, int, CancellationToken, Task<Stream>> _openRange;
    private readonly ParallelDownloadOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<Task<Chunk>> _pending = new();

    private long _scheduled;
    private long _position;
    private Chunk _current;
    private int _currentOffset;
    private bool _disposed;

    public ParallelRangeStream(long length, Func<long, int, CancellationToken, Task<Stream>> openRange,
        ParallelDownloadOptions options)
    {
        _length = length;
        _openRange = openRange;
        _options = options;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>Schedules the first chunks and waits for the first one, so a server without ranges shows up here.</summary>
    internal async Task StartAsync(CancellationToken ct)
    {
        Schedule();
        await NextChunkAsync(ct).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty || _position >= _length)
            return 0;

        if (_currentOffset >= _current.Length)
            await NextChunkAsync(ct).ConfigureAwait(false);

        var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.Buffer.AsMemory(_currentOffset, count).CopyTo(buffer);
        _currentOffset += count;
        _position += count;

        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task NextChunkAsync(CancellationToken ct)
    {
        ReturnCurrent();

        // The awaited chunk still counts as in flight, so the next one is scheduled only once it has arrived
        var next = _pending.Peek();
        _current = await next.WaitAsync(ct).ConfigureAwait(false);
        _currentOffset = 0;

        _pending.Dequeue();
        Schedule();
    }

    private void Schedule()
    {
        while (_pending.Count < _options.Parallelism && _scheduled < _length)
        {
            var count = (int)Math.Min(_options.ChunkSize, _length - _scheduled);
            _pending.Enqueue(FetchAsync(_scheduled, count, _stop.Token));
            _scheduled += count;
        }
    }

    private async Task<Chunk> FetchAsync(long offset, int count, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(count);

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var body = await _openRange(offset, count, ct).ConfigureAwait(false);

                    await using (body.ConfigureAwait(false))
                        await body.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);

                    return new Chunk(buffer, count);
                }
                catch (Exception e) when (attempt < _options.AttemptsPerChunk && IsTransient(e) && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    // EndOfStreamException (a body shorter than the range) is an IOException too
    private static bool IsTransient(Exception e) => e is IOException or HttpRequestException or TimeoutException;

    private void ReturnCurrent()
    {
        if (_current.Buffer is { } buffer)
            ArrayPool<byte>.Shared.Return(buffer);

        _current = default;
        _currentOffset = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _stop.Cancel();
            ReturnCurrent();

            while (_pending.TryDequeue(out var task))
            {
                _ = task.ContinueWith(static t =>
                {
                    if (t.IsCompletedSuccessfully)
                        ArrayPool<byte>.Shared.Return(t.Result.Buffer);
                    else
                        _ = t.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            _stop.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private readonly record struct Chunk(byte[] Buffer, int Length);
}

/// <summary>A response body that disposes its response with it.</summary>
internal sealed class ResponseBodyStream(HttpResponseMessage response, Stream body) : Stream
{
    public override bool CanRead => body.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => response.Content.Headers.ContentLength ?? throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => body.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => body.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        body.ReadAsync(buffer, offset, count, ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        body.ReadAsync(buffer, ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            body.Dispose();
            response.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
