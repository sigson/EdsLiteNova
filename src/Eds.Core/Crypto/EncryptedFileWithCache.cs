using Eds.Core.Fs;

namespace Eds.Core.Crypto;

/// <summary>
/// Transparent encrypting random-access wrapper with an LRU/ref-count cache of
/// decrypted buffers. Port of <c>crypto.EncryptedFileWithCache</c>.
///
/// The virtual (decrypted) address space is divided into fixed buffers of
/// <c>bufferSizeInBlocks * sectorSize</c> bytes. Each buffer is decrypted once
/// on first touch and kept in a small cache (default 25 buffers × 40 sectors),
/// so navigating a mounted filesystem doesn't re-read and re-decrypt the same
/// sectors repeatedly — the difference between a usable file manager and an
/// unusable one (see port guide §2.2). Eviction mirrors the original: the
/// buffer with the fewest references is dropped (its dirty contents flushed
/// first).
///
/// Correctness is identical to <see cref="EncryptedFile"/>: the same per-sector
/// engine IV is applied (<c>xts-plain64</c> advances the sector index natively
/// across a multi-sector buffer), and only whole sectors up to the file length
/// are ever written back, so nothing is corrupted beyond EOF.
/// </summary>
public sealed class EncryptedFileWithCache : IRandomAccessIO
{
    public const int DefaultNumCachedBuffers = 25;
    public const int DefaultBufferSizeInBlocks = 40;

    private sealed class CachedBuffer(int size)
    {
        public readonly byte[] Data = new byte[size];
        public int RefCount = 1;
        public bool Dirty;
    }

    private readonly IRandomAccessIO _base;
    private readonly IEncryptedFileLayout _layout;
    private readonly IFileEncryptionEngine _engine;
    private readonly long _dataOffset;
    private readonly int _sectorSize;
    private readonly int _bufferSize;
    private readonly int _maxCachedBuffers;
    private readonly Dictionary<long, CachedBuffer> _cache;

    private long _position;
    private long _length;

    public EncryptedFileWithCache(IRandomAccessIO baseIo, IEncryptedFileLayout layout)
        : this(baseIo, layout, DefaultNumCachedBuffers, DefaultBufferSizeInBlocks) { }

    public EncryptedFileWithCache(
        IRandomAccessIO baseIo,
        IEncryptedFileLayout layout,
        int maxNumberCachedBuffers,
        int bufferSizeInBlocks)
    {
        _base = baseIo;
        _layout = layout;
        _engine = layout.Engine;
        _dataOffset = layout.EncryptedDataOffset;
        _sectorSize = _engine.FileBlockSize;
        _bufferSize = bufferSizeInBlocks * _sectorSize;
        _maxCachedBuffers = maxNumberCachedBuffers;
        _cache = new Dictionary<long, CachedBuffer>(maxNumberCachedBuffers);
        // Multi-sector buffers must advance the IV per sector. XTS does this
        // natively; CBC (LUKS cbc-plain) needs the flag so the mode increments
        // the IV every sectorSize bytes.
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

        int done = 0;
        while (done < len)
        {
            long virt = _position + done;
            long bufIndex = virt / _bufferSize;
            int inner = (int)(virt - bufIndex * _bufferSize);
            int chunk = Math.Min(len - done, _bufferSize - inner);

            CachedBuffer cb = GetOrLoad(bufIndex);
            Array.Copy(cb.Data, inner, buffer, offset + done, chunk);
            done += chunk;
        }

        _position += done;
        return done;
    }

    public void WriteByte(int b) => Write(new[] { (byte)b }, 0, 1);

    public void Write(byte[] buffer, int offset, int len)
    {
        if (len <= 0) return;

        int done = 0;
        while (done < len)
        {
            long virt = _position + done;
            long bufIndex = virt / _bufferSize;
            int inner = (int)(virt - bufIndex * _bufferSize);
            int chunk = Math.Min(len - done, _bufferSize - inner);

            CachedBuffer cb = GetOrLoad(bufIndex);
            Array.Copy(buffer, offset + done, cb.Data, inner, chunk);
            cb.Dirty = true;
            done += chunk;

            long newVirtEnd = virt + chunk;
            if (newVirtEnd > _length) _length = newVirtEnd;
        }

        _position += done;
    }

    public void Flush()
    {
        FlushCachedChanges();
        _base.Flush();
    }

    public void SetLength(long newLength)
    {
        _base.SetLength(CalcBasePosition(newLength));
        _length = newLength;
        if (_position > _length) _position = _length;
        // Drop cached buffers that now lie entirely beyond the new length.
        var stale = new List<long>();
        foreach (var kv in _cache)
            if (kv.Key * _bufferSize >= _length) stale.Add(kv.Key);
        foreach (var idx in stale)
        {
            Array.Clear(_cache[idx].Data);
            _cache.Remove(idx);
        }
    }

    private CachedBuffer GetOrLoad(long bufIndex)
    {
        if (_cache.TryGetValue(bufIndex, out var existing))
        {
            existing.RefCount++;
            return existing;
        }

        CachedBuffer cb = ReserveSlot(bufIndex);
        LoadFromBase(bufIndex, cb);
        return cb;
    }

    private void LoadFromBase(long bufIndex, CachedBuffer cb)
    {
        long bufStartVirt = bufIndex * _bufferSize;
        long valid = Math.Min(_bufferSize, Math.Max(0, _length - bufStartVirt));
        Array.Clear(cb.Data);
        if (valid <= 0) return;

        // Decrypt whole sectors covering the valid range.
        int decLen = (int)RoundUp(valid, _sectorSize);
        if (decLen > _bufferSize) decLen = _bufferSize;

        _base.Seek(CalcBasePosition(bufStartVirt));
        int read = ReadFull(_base, cb.Data, 0, decLen);
        if (read < decLen) Array.Clear(cb.Data, read, decLen - read);

        _layout.SetEncryptionEngineIV(_engine, bufStartVirt);
        _engine.Decrypt(cb.Data, 0, decLen);
    }

    private CachedBuffer ReserveSlot(long bufIndex)
    {
        if (_cache.Count < _maxCachedBuffers)
        {
            var fresh = new CachedBuffer(_bufferSize);
            _cache[bufIndex] = fresh;
            return fresh;
        }

        // Evict the least-referenced buffer (matches the original policy).
        long victimIndex = -1;
        int minRefs = int.MaxValue;
        foreach (var kv in _cache)
        {
            if (kv.Value.RefCount < minRefs)
            {
                minRefs = kv.Value.RefCount;
                victimIndex = kv.Key;
            }
        }

        CachedBuffer victim = _cache[victimIndex];
        if (victim.Dirty) WriteBufferToBase(victimIndex, victim);
        _cache.Remove(victimIndex);

        Array.Clear(victim.Data);
        victim.RefCount = 1;
        victim.Dirty = false;
        _cache[bufIndex] = victim;
        return victim;
    }

    private void FlushCachedChanges()
    {
        foreach (var kv in _cache)
            if (kv.Value.Dirty) WriteBufferToBase(kv.Key, kv.Value);
    }

    private void WriteBufferToBase(long bufIndex, CachedBuffer cb)
    {
        long bufStartVirt = bufIndex * _bufferSize;
        long valid = Math.Min(_bufferSize, Math.Max(0, _length - bufStartVirt));
        if (valid <= 0) { cb.Dirty = false; return; }

        int encLen = (int)RoundUp(valid, _sectorSize);
        if (encLen > _bufferSize) encLen = _bufferSize;

        // Encrypt a copy so the cache keeps holding the decrypted plaintext.
        var tmp = new byte[encLen];
        Array.Copy(cb.Data, tmp, encLen);
        _layout.SetEncryptionEngineIV(_engine, bufStartVirt);
        _engine.Encrypt(tmp, 0, encLen);
        _base.Seek(CalcBasePosition(bufStartVirt));
        _base.Write(tmp, 0, encLen);
        Array.Clear(tmp);

        cb.Dirty = false;
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

    public void Dispose()
    {
        try
        {
            FlushCachedChanges();
        }
        finally
        {
            foreach (var cb in _cache.Values) Array.Clear(cb.Data);
            _cache.Clear();
            _base.Dispose();
        }
    }
}
