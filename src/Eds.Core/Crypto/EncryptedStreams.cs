namespace Eds.Core.Crypto;

/// <summary>
/// Sequential decrypting <see cref="Stream"/> over an encrypted base stream.
/// Port of <c>crypto.EncryptedInputStream</c> (which extends
/// <c>TransInputStream</c>). The base is consumed in
/// <c>engine.FileBlockSize</c>-sized blocks, each decrypted with the engine IV
/// set for that block's offset. Used for whole-file import/export and by EncFS
/// where random access isn't needed.
///
/// <para>Whole-block streams only: like the original, each block handed to the
/// engine is a full crypto block. EncFS encodes the (short) final block through
/// a separate stream cipher, so this class never sees a sub-block remainder.</para>
/// </summary>
public sealed class EncryptedInputStream : Stream
{
    private readonly Stream _base;
    private readonly bool _ownsBase;
    private readonly IEncryptedFileLayout _layout;
    private readonly IFileEncryptionEngine _engine;
    private readonly int _bufferSize;
    private readonly bool _allowEmptyParts;

    private readonly byte[] _buffer;
    private int _bufferLen;      // valid decrypted bytes in _buffer
    private int _bufferOffset;   // next byte to serve
    private long _virtPosition;  // decrypted bytes served so far (block-aligned at fills)

    public EncryptedInputStream(Stream baseStream, IEncryptedFileLayout layout,
        bool ownsBase = true, bool allowEmptyParts = true)
    {
        _base = baseStream;
        _ownsBase = ownsBase;
        _layout = layout;
        _engine = layout.Engine;
        _bufferSize = _engine.FileBlockSize;
        _allowEmptyParts = allowEmptyParts;
        _buffer = new byte[_bufferSize];
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _virtPosition - (_bufferLen - _bufferOffset);
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            if (_bufferOffset >= _bufferLen)
            {
                if (!FillBuffer()) break;
                if (_bufferLen == 0) break;
            }
            int chunk = Math.Min(count - total, _bufferLen - _bufferOffset);
            Array.Copy(_buffer, _bufferOffset, buffer, offset + total, chunk);
            _bufferOffset += chunk;
            total += chunk;
        }
        return total;
    }

    private bool FillBuffer()
    {
        long blockPos = _virtPosition;
        int read = ReadFull(_base, _buffer, _bufferSize);
        _bufferOffset = 0;
        _bufferLen = read;
        if (read <= 0) return false;

        bool empty = _allowEmptyParts && read == _bufferSize && IsAllZero(_buffer, read);
        if (!empty)
        {
            _layout.SetEncryptionEngineIV(_engine, blockPos);
            _engine.Decrypt(_buffer, 0, read);
        }
        _virtPosition += read;
        return true;
    }

    private static bool IsAllZero(byte[] b, int count)
    {
        for (int i = 0; i < count; i++) if (b[i] != 0) return false;
        return true;
    }

    private static int ReadFull(Stream s, byte[] buf, int len)
    {
        int total = 0;
        while (total < len)
        {
            int n = s.Read(buf, total, len - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Array.Clear(_buffer);
            if (_ownsBase) _base.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Sequential encrypting <see cref="Stream"/> over a base stream. Port of
/// <c>crypto.EncryptedOutputStream</c> (<c>TransOutputStream</c>). Buffers up to
/// one crypto block, encrypts it with the engine IV for that block offset, and
/// writes it to the base. The trailing partial block is flushed on close.
/// </summary>
public sealed class EncryptedOutputStream : Stream
{
    private readonly Stream _base;
    private readonly bool _ownsBase;
    private readonly IEncryptedFileLayout _layout;
    private readonly IFileEncryptionEngine _engine;
    private readonly int _bufferSize;
    private readonly bool _allowEmptyParts;

    private readonly byte[] _buffer;
    private int _bufferOffset;
    private long _virtPosition;

    public EncryptedOutputStream(Stream baseStream, IEncryptedFileLayout layout,
        bool ownsBase = true, bool allowEmptyParts = false)
    {
        _base = baseStream;
        _ownsBase = ownsBase;
        _layout = layout;
        _engine = layout.Engine;
        _bufferSize = _engine.FileBlockSize;
        _allowEmptyParts = allowEmptyParts;
        _buffer = new byte[_bufferSize];
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _virtPosition + _bufferOffset;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        int done = 0;
        while (done < count)
        {
            int chunk = Math.Min(count - done, _bufferSize - _bufferOffset);
            Array.Copy(buffer, offset + done, _buffer, _bufferOffset, chunk);
            _bufferOffset += chunk;
            done += chunk;
            if (_bufferOffset == _bufferSize) FlushBlock();
        }
    }

    private void FlushBlock()
    {
        if (_bufferOffset == 0) return;
        int count = _bufferOffset;

        bool empty = _allowEmptyParts && count == _bufferSize && IsAllZero(_buffer, count);
        if (!empty)
        {
            _layout.SetEncryptionEngineIV(_engine, _virtPosition);
            _engine.Encrypt(_buffer, 0, count);
        }
        _base.Write(_buffer, 0, count);
        _virtPosition += count;
        _bufferOffset = 0;
        Array.Clear(_buffer);
    }

    private static bool IsAllZero(byte[] b, int count)
    {
        for (int i = 0; i < count; i++) if (b[i] != 0) return false;
        return true;
    }

    public override void Flush()
    {
        FlushBlock();
        _base.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FlushBlock();
            _base.Flush();
            Array.Clear(_buffer);
            if (_ownsBase) _base.Dispose();
        }
        base.Dispose(disposing);
    }
}
