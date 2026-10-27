using FileVault.Core;

namespace FileVault.S3.Tests;

/// <summary>
/// A file of some other provider: its stream cannot seek, may hide its length (<c>reportLength: false</c>), fail
/// part-way (<c>failAfter</c>) or tell how far it has been read (<c>onRead</c>).
/// </summary>
internal sealed class MemoryFileItem(string name, byte[] content, bool reportLength = true, long failAfter = -1,
    Action<long, CancellationToken>? onRead = null) : IFileItem
{
    public string Name => name;
    public string FullName => "memory:/" + name;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long Size => content.Length;
    long? IFileProviderItem.Size => content.Length;
    public string Extension => Path.GetExtension(name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
        Task.FromResult<(Stream, long)>((new SourceStream(content, failAfter, onRead), reportLength ? content.Length : 0));

    public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;

    private sealed class SourceStream(byte[] content, long failAfter, Action<long, CancellationToken>? onRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (failAfter >= 0 && _position >= failAfter)
                throw new IOException("The source went away.");

            var count = Math.Min(Math.Min(buffer.Length, content.Length - _position), 64 * 1024);
            content.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead?.Invoke(_position, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
