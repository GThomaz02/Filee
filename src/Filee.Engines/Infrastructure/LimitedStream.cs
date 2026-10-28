// A read-only wrapper that counts the bytes read through it and fails once they pass a limit, so a ZIP entry that
// unpacks to far more than its header says (a ZIP bomb) stops at a known size instead of filling the memory.

namespace Filee.Engines.Infrastructure;

/// <summary>Reads at most <c>limit</c> bytes of <c>inner</c>, then throws (<see cref="InvalidDataException"/> by default).</summary>
internal sealed class LimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private readonly Func<Exception> _over;
    private readonly Action<long>? _onRead;
    private long _read;

    public LimitedStream(Stream inner, long limit, string message) : this(inner, limit, () => new InvalidDataException(message))
    {
    }

    /// <param name="over">The exception thrown once more than <paramref name="limit"/> bytes were read.</param>
    /// <param name="onRead">Called after every read with the number of bytes read so far (for limits shared by several streams).</param>
    public LimitedStream(Stream inner, long limit, Func<Exception> over, Action<long>? onRead = null)
    {
        _inner = inner;
        _limit = limit;
        _over = over;
        _onRead = onRead;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        _read += read;
        if (_read > _limit)
            throw _over();
        if (read > 0)
            _onRead?.Invoke(_read);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
