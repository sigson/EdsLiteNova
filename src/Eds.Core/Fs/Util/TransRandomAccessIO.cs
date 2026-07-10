namespace Eds.Core.Fs.Util;

/// <summary>Decorator over an <see cref="IRandomAccessIO"/>. Port of <c>fs.util.RandomAccessIOWrapper</c>.</summary>
public class RandomAccessIOWrapper : IRandomAccessIO
{
    private readonly IRandomAccessIO _base;

    public RandomAccessIOWrapper(IRandomAccessIO baseIo) => _base = baseIo;

    public IRandomAccessIO GetBase() => _base;

    public virtual void Seek(long position) => _base.Seek(position);
    public virtual long GetFilePointer() => _base.GetFilePointer();
    public virtual long Length() => _base.Length();
    public virtual int ReadByte() => _base.ReadByte();
    public virtual int Read(byte[] b, int off, int len) => _base.Read(b, off, len);
    public virtual void WriteByte(int b) => _base.WriteByte(b);
    public virtual void Write(byte[] b, int off, int len) => _base.Write(b, off, len);
    public virtual void Flush() => _base.Flush();
    public virtual void SetLength(long newLength) => _base.SetLength(newLength);
    public virtual void Dispose() => _base.Dispose();
}

/// <summary>
/// Buffered random-access IO base. Port of <c>fs.util.BufferedRandomAccessIO</c>.
/// Maintains a virtual position/length and a single current buffer supplied by
/// the subclass; reads/writes are served from the buffer and flushed on boundary
/// crossings. The Android Logger/GlobalConfig debug hooks are dropped.
/// </summary>
public abstract class BufferedRandomAccessIO : RandomAccessIOWrapper
{
    protected long _currentPosition, _length;
    protected readonly int _bufferSize;

    protected BufferedRandomAccessIO(IRandomAccessIO baseIo, int bufferSize) : base(baseIo)
        => _bufferSize = bufferSize;

    public override long GetFilePointer() => _currentPosition;

    public override void Dispose() => Close(true);

    public virtual void Close(bool closeBase)
    {
        if (closeBase) base.Dispose();
    }

    public override int Read(byte[] buf, int offset, int count)
    {
        if (_currentPosition >= _length) return -1;
        if (count > 0)
        {
            byte[] currentBuffer = GetCurrentBuffer();
            long avail = Math.Min(GetSpaceInBuffer(), _length - _currentPosition);
            int read = (int)Math.Min(avail, count);
            Array.Copy(currentBuffer, GetPositionInBuffer(), buf, offset, read);
            SetCurrentBufferRead(read);
            return read;
        }
        return 0;
    }

    public override int ReadByte()
    {
        var b = new byte[1];
        return Read(b, 0, 1) == 1 ? b[0] : -1;
    }

    public override void WriteByte(int b) => Write(new[] { (byte)b }, 0, 1);

    public override void Write(byte[] buf, int offset, int count)
    {
        while (count > 0)
        {
            byte[] currentBuffer = GetCurrentBuffer();
            int written = Math.Min(GetSpaceInBuffer(), count);
            Array.Copy(buf, offset, currentBuffer, GetPositionInBuffer(), written);
            offset += written;
            count -= written;
            SetCurrentBufferWritten(written);
        }
    }

    public override void Seek(long position)
    {
        if (position < 0) throw new ArgumentException("Negative position");
        _currentPosition = position;
    }

    public override long Length() => _length;

    public override void Flush()
    {
        WriteCurrentBuffer();
        base.Flush();
    }

    protected abstract byte[] GetCurrentBuffer();
    protected abstract long GetBufferPosition();

    protected virtual void SetCurrentBufferWritten(int numBytes)
    {
        _currentPosition += numBytes;
        if (_currentPosition > _length) _length = _currentPosition;
    }

    protected void SetCurrentBufferRead(int numBytes) => _currentPosition += numBytes;

    protected int GetPositionInBuffer() => (int)(_currentPosition % _bufferSize);

    protected virtual void WriteCurrentBuffer()
    {
        byte[] buf = GetCurrentBuffer();
        var b = GetBase();
        b.Seek(GetBufferPosition());
        b.Write(buf, 0, buf.Length);
    }

    protected int GetSpaceInBuffer() => _bufferSize - GetPositionInBuffer();
}

/// <summary>
/// Transforming buffered IO. Port of <c>fs.util.TransRandomAccessIO</c>. Adds the
/// current buffer array and the load/store hooks
/// (<see cref="TransformBufferFromBase"/> / <see cref="TransformBufferToBase"/>)
/// that subclasses override to encrypt/MAC each buffer as it moves to/from the
/// base. With the default (identity) transforms it is a plain buffering wrapper.
/// </summary>
public class TransRandomAccessIO : BufferedRandomAccessIO
{
    protected byte[] _buffer;
    protected bool _allowSkip, _isBufferLoaded, _isBufferChanged;
    private long _bufferPosition;

    public TransRandomAccessIO(IRandomAccessIO baseIo, int bufferSize) : base(baseIo, bufferSize)
        => _buffer = new byte[_bufferSize];

    public override void Close(bool closeBase)
    {
        try
        {
            WriteCurrentBuffer();
            base.Close(closeBase);
        }
        finally { Array.Clear(_buffer); }
    }

    public override void Write(byte[] buf, int offset, int count)
    {
        if (!_allowSkip && _currentPosition > _length) FillFreeSpace();
        base.Write(buf, offset, count);
    }

    public override void SetLength(long newLength)
    {
        if (!_allowSkip && newLength > _length - 1)
        {
            Seek(newLength - 1);
            WriteByte(0);
        }
        else
        {
            _length = newLength;
            base.SetLength(CalcBasePosition(newLength));
        }
    }

    public void SetAllowSkip(bool val) => _allowSkip = val;

    protected override void SetCurrentBufferWritten(int numBytes)
    {
        base.SetCurrentBufferWritten(numBytes);
        _isBufferChanged = true;
    }

    protected override byte[] GetCurrentBuffer()
    {
        if (_isBufferLoaded)
        {
            long dif = _currentPosition - _bufferPosition;
            if (dif < 0 || dif >= _bufferSize)
            {
                WriteCurrentBuffer();
                _bufferPosition = CalcBufferPosition();
                _isBufferLoaded = false;
            }
        }
        else
            _bufferPosition = CalcBufferPosition();
        LoadCurrentBuffer();
        return _buffer;
    }

    protected override void WriteCurrentBuffer()
    {
        if (!_isBufferChanged) return;
        long bp = GetBufferPosition();
        int count = (int)Math.Min(_length - bp, _bufferSize);
        TransformBufferAndWriteToBase(_buffer, 0, count, bp);
        _isBufferChanged = false;
    }

    protected void LoadCurrentBuffer()
    {
        if (_isBufferLoaded) return;
        long bp = GetBufferPosition();
        int space = (int)Math.Min(_length - bp, _bufferSize);
        if (space > 0)
        {
            int act = ReadFromBaseAndTransformBuffer(_buffer, 0, space, bp);
            Array.Clear(_buffer, act, _bufferSize - act);
        }
        _isBufferChanged = false;
        _isBufferLoaded = true;
    }

    protected virtual int ReadFromBaseAndTransformBuffer(byte[] buf, int offset, int count, long bufferPosition)
    {
        int bc = ReadFromBase(buf, offset, count, bufferPosition);
        return TransformBufferFromBase(buf, offset, bc, bufferPosition, buf);
    }

    protected virtual void TransformBufferAndWriteToBase(byte[] buf, int offset, int count, long bufferPosition)
    {
        TransformBufferToBase(buf, offset, count, bufferPosition, buf);
        WriteToBase(buf, offset, count, bufferPosition);
    }

    protected virtual void TransformBufferToBase(byte[] buf, int offset, int count, long bufferPosition, byte[] baseBuffer) { }

    protected virtual int TransformBufferFromBase(byte[] baseBuffer, int offset, int count, long bufferPosition, byte[] dstBuffer) => count;

    protected virtual void WriteToBase(byte[] buf, int offset, int count, long bufferPosition)
    {
        GetBase().Seek(CalcBasePosition(bufferPosition));
        GetBase().Write(buf, offset, count);
    }

    protected virtual int ReadFromBase(byte[] buf, int offset, int count, long bufferPosition)
    {
        GetBase().Seek(CalcBasePosition(bufferPosition));
        return ReadFullyEncrypted(buf, offset, count);
    }

    protected virtual long CalcBasePosition(long position) => position;
    protected virtual long CalcVirtPosition(long basePosition) => basePosition;

    protected override long GetBufferPosition() => _bufferPosition;

    protected int ReadFullyEncrypted(byte[] buf, int off, int len)
    {
        int t = 0;
        while (t < len)
        {
            int n = GetBase().Read(buf, off + t, len - t);
            if (n < 0) return t;
            t += n;
        }
        return t;
    }

    protected void FillFreeSpace()
    {
        long pos = _length;
        int rem = (int)(_length % _bufferSize);
        if (rem != 0) pos += _bufferSize - rem;
        var tbuf = new byte[_bufferSize];
        for (long bp = GetBufferPosition(); pos < bp; pos += _bufferSize)
            TransformBufferAndWriteToBase(tbuf, 0, _bufferSize, pos);
    }

    private long CalcBufferPosition() => _currentPosition - (_currentPosition % _bufferSize);
}
