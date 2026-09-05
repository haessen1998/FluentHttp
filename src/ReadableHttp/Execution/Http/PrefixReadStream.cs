namespace ReadableHttp.Execution;

// Replays format-detection bytes without buffering the response or owning its stream.
internal sealed class PrefixReadStream(byte[] prefix, Stream source) : Stream
{
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= prefix.Length) return source.Read(buffer, offset, count);
        var length = Math.Min(count, prefix.Length - _position);
        prefix.AsSpan(_position, length).CopyTo(buffer.AsSpan(offset, count));
        _position += length;
        return length;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_position >= prefix.Length) return source.ReadAsync(buffer, cancellationToken);
        var length = Math.Min(buffer.Length, prefix.Length - _position);
        prefix.AsMemory(_position, length).CopyTo(buffer);
        _position += length;
        return ValueTask.FromResult(length);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
