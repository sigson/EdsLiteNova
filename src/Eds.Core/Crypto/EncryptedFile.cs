using Eds.Core.Fs;

namespace Eds.Core.Crypto;

/// <summary>
/// How encryption is laid over a file/volume. Mirrors
/// <c>container.EncryptedFileLayout</c> (the subset EncryptedFile needs).
/// </summary>
public interface IEncryptedFileLayout
{
    /// <summary>Physical offset where the encrypted payload begins.</summary>
    long EncryptedDataOffset { get; }

    IFileEncryptionEngine Engine { get; }

    /// <summary>
    /// Sets the engine IV for the given offset within the decrypted volume.
    /// For XTS this maps to the starting sector index.
    /// </summary>
    void SetEncryptionEngineIV(IFileEncryptionEngine engine, long decryptedVolumeOffset);
}

/// <summary>
/// Transparent, sector-aligned encrypting random-access wrapper over a base
/// <see cref="IRandomAccessIO"/>. Reproduces the behaviour of
/// <c>crypto.EncryptedFile</c> in a self-contained form (without porting the
/// full TransRandomAccessIO buffering machinery): reads/writes are expanded to
/// whole crypto sectors, each processed with its correct sector IV.
///
/// The optimised LRU-cached variant (EncryptedFileWithCache) can be layered on
/// top later; this class is correct but unbuffered.
/// </summary>
public class EncryptedFile : IRandomAccessIO
{
    /// <summary>True if the given span is all zero bytes. Mirrors
    /// <c>EncryptedFile.isBufferEmpty</c> (used to skip crypto/MAC on sparse blocks).</summary>
    public static bool IsBufferEmpty(byte[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
            if (buffer[offset + i] != 0) return false;
        return true;
    }

    private readonly IRandomAccessIO _base;
    private readonly IEncryptedFileLayout _layout;
    private readonly IFileEncryptionEngine _engine;
    private readonly long _dataOffset;
    private readonly int _sectorSize;
    private long _position;
    private long _length;

    public EncryptedFile(IRandomAccessIO baseIo, IEncryptedFileLayout layout)
    {
        _base = baseIo;
        _layout = layout;
        _engine = layout.Engine;
        _dataOffset = layout.EncryptedDataOffset;
        _sectorSize = _engine.FileBlockSize;
        // Multi-sector buffers must advance the IV per sector. XTS does this
        // internally regardless; CBC (LUKS cbc-plain) needs the flag set so the
        // native mode increments the IV every FileBlockSize bytes.
        _engine.SetIncrementIV(true);
        _length = CalcVirtPosition(_base.Length());
    }

    private long CalcBasePosition(long virt) => virt + _dataOffset;
    private long CalcVirtPosition(long basePos) => basePos - _dataOffset;

    public void Seek(long position) => _position = position;
    public long GetFilePointer() => _position;
    public long Length() => _length;

    public int ReadByte()
    {
        var one = new byte[1];
        int n = Read(one, 0, 1);
        return n <= 0 ? -1 : one[0];
    }

    public int Read(byte[] buffer, int offset, int len)
    {
        if (_position >= _length) return -1;
        if (_position + len > _length) len = (int)(_length - _position);
        if (len <= 0) return 0;

        long virtStart = _position;
        long alignedStart = virtStart - virtStart % _sectorSize;
        long alignedEnd = RoundUp(virtStart + len, _sectorSize);
        int alignedLen = (int)(alignedEnd - alignedStart);

        var tmp = new byte[alignedLen];
        _base.Seek(CalcBasePosition(alignedStart));
        int read = ReadFull(_base, tmp, 0, alignedLen);
        if (read < alignedLen)
        {
            // zero-fill the tail beyond the physical file (sparse/last sector)
            Array.Clear(tmp, read, alignedLen - read);
        }

        DecryptSectors(tmp, alignedStart);

        int inner = (int)(virtStart - alignedStart);
        Array.Copy(tmp, inner, buffer, offset, len);
        Array.Clear(tmp);
        _position += len;
        return len;
    }

    public void WriteByte(int b) => Write(new[] { (byte)b }, 0, 1);

    public void Write(byte[] buffer, int offset, int len)
    {
        if (len <= 0) return;

        long virtStart = _position;
        long alignedStart = virtStart - virtStart % _sectorSize;
        long alignedEnd = RoundUp(virtStart + len, _sectorSize);
        int alignedLen = (int)(alignedEnd - alignedStart);

        var tmp = new byte[alignedLen];

        // read-modify-write: bring in existing sectors we only partially overwrite
        bool headPartial = virtStart != alignedStart;
        bool tailPartial = (virtStart + len) != alignedEnd;
        if (headPartial || tailPartial)
        {
            _base.Seek(CalcBasePosition(alignedStart));
            int read = ReadFull(_base, tmp, 0, alignedLen);
            if (read < alignedLen) Array.Clear(tmp, read, alignedLen - read);
            DecryptSectors(tmp, alignedStart);
        }

        int inner = (int)(virtStart - alignedStart);
        Array.Copy(buffer, offset, tmp, inner, len);

        EncryptSectors(tmp, alignedStart);
        _base.Seek(CalcBasePosition(alignedStart));
        _base.Write(tmp, 0, alignedLen);
        Array.Clear(tmp);

        _position += len;
        if (_position > _length) _length = _position;
    }

    public void Flush() => _base.Flush();

    public void SetLength(long newLength)
    {
        _base.SetLength(CalcBasePosition(newLength));
        _length = newLength;
        if (_position > _length) _position = _length;
    }

    private void DecryptSectors(byte[] buf, long virtOffset)
    {
        _layout.SetEncryptionEngineIV(_engine, virtOffset);
        _engine.Decrypt(buf, 0, buf.Length);
    }

    private void EncryptSectors(byte[] buf, long virtOffset)
    {
        _layout.SetEncryptionEngineIV(_engine, virtOffset);
        _engine.Encrypt(buf, 0, buf.Length);
    }

    private static long RoundUp(long v, int align) => (v + align - 1) / align * align;

    private static int ReadFull(IRandomAccessIO io, byte[] buf, int off, int len)
    {
        int total = 0;
        while (total < len)
        {
            int n = io.Read(buf, off + total, len - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public void Dispose() => _base.Dispose();
}
