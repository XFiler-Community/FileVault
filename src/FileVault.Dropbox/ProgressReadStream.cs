namespace FileVault.Dropbox;

/// <summary>
/// Reads through to <paramref name="inner"/> and reports how far it got. Disposing it leaves
/// <paramref name="inner"/> open: the request content that wraps it is disposed with the request, the source stream
/// belongs to the caller.
/// </summary>
internal sealed class ProgressReadStream(Stream inner, long total, IProgress<double>? progress) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => total;

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private int Count(int read)
    {
        _read += read;

        if (total > 0 && read > 0)
            progress?.Report(Math.Min(1, (double)_read / total));

        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
