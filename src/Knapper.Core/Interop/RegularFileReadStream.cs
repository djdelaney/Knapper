using Microsoft.Win32.SafeHandles;

namespace Knapper.Core.Interop;

/// <summary>
/// A read-only, seekable stream over a handle from
/// <see cref="Posix.OpenRegularForRead"/>, reading by pread(2) at an offset
/// this stream tracks itself.
///
/// It exists because that handle carries O_NONBLOCK, and the BCL may not be
/// handed such a handle: from .NET 11 the runtime classifies a non-blocking
/// descriptor as pipe-like, and both FileStream and RandomAccess stop
/// honouring offsets on it. Measured on 11.0.0: a stat past the read cap
/// answered with the SHA-256 of EMPTY input — a well-formed hash, no error —
/// and every positional read restarted wherever the descriptor happened to
/// be. pread(2) names its offset on every call and never consults the
/// descriptor's flags or position, so the answer cannot depend on how a
/// runtime chooses to classify the handle.
///
/// Does not own the handle; the caller that opened it disposes it.
/// </summary>
internal sealed class RegularFileReadStream(SafeFileHandle handle) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => Posix.FStat(handle).Size;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        var read = Posix.PRead(handle, buffer, _position);
        _position += read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
