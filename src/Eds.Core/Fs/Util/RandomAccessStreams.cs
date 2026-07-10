namespace Eds.Core.Fs.Util;

/// <summary>
/// Read-only, seekable <see cref="Stream"/> over an <see cref="IRandomAccessIO"/>.
/// Port of <c>fs.util.RandomAccessInputStream</c>. Lets code that expects a
/// <see cref="Stream"/> consume a random-access source (e.g. importing a file
/// out of a mounted volume).
/// </summary>
public sealed class RandomAccessInputStream : Stream
{
    private readonly IRandomAccessIO _io;
    private readonly bool _ownsIo;

    public RandomAccessInputStream(IRandomAccessIO io, bool ownsIo = true)
    {
        _io = io;
        _ownsIo = ownsIo;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _io.Length();

    public override long Position
    {
        get => _io.GetFilePointer();
        set => _io.Seek(value);
    }

    public override int ReadByte()
    {
        int b = _io.ReadByte();
        return b < 0 ? -1 : b;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _io.Read(buffer, offset, count);
        return n < 0 ? 0 : n; // Stream contract: 0 at EOF (IRandomAccessIO uses -1)
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _io.GetFilePointer() + offset,
            SeekOrigin.End => _io.Length() + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        _io.Seek(target);
        return target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsIo) _io.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Writable, seekable <see cref="Stream"/> over an <see cref="IRandomAccessIO"/>.
/// Port of <c>fs.util.RandomAccessOutputStream</c>.
/// </summary>
public sealed class RandomAccessOutputStream : Stream
{
    private readonly IRandomAccessIO _io;
    private readonly bool _ownsIo;

    public RandomAccessOutputStream(IRandomAccessIO io, bool ownsIo = true)
    {
        _io = io;
        _ownsIo = ownsIo;
    }

    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _io.Length();

    public override long Position
    {
        get => _io.GetFilePointer();
        set => _io.Seek(value);
    }

    public override void WriteByte(byte value) => _io.WriteByte(value);
    public override void Write(byte[] buffer, int offset, int count) => _io.Write(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _io.GetFilePointer() + offset,
            SeekOrigin.End => _io.Length() + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        _io.Seek(target);
        return target;
    }

    public override void Flush() => _io.Flush();
    public override void SetLength(long value) => _io.SetLength(value);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsIo) _io.Dispose();
        base.Dispose(disposing);
    }
}
