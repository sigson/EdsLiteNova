using System.Buffers.Binary;
using Eds.Core.Crypto;
using Eds.Core.Crypto.Engines;
using Eds.Core.Fs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase A crypto-completion tests: CFB-128 mode, EncryptedFileWithCache (LRU),
/// and the sequential Encrypted*Stream wrappers. See porting gap guide §2.
/// </summary>
public class PhaseACryptoTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // -- CFB -------------------------------------------------------------

    [Fact]
    public void Cfb128_MatchesNist80038a()
    {
        // CFB128-AES128 (NIST SP 800-38A F.3.13/F.3.14).
        var key = Hex("2b7e151628aed2a6abf7158809cf4f3c");
        var iv = Hex("000102030405060708090a0b0c0d0e0f");
        var pt = Hex("6bc1bee22e409f96e93d7e117393172a" +
                     "ae2d8a571e03ac9c9eb76fac45af8e51" +
                     "30c81c46a35ce411e5fbc1191a0a52ef" +
                     "f69f2445df4f9b17ad2b417be66c3710");
        const string expect = "3b3fd92eb72dad20333449f8e83cfb4a" +
                              "c8a64537a0b3a93fcde3cdad9f1ce58b" +
                              "26751f67a3cbb140b1808cf187a4f4df" +
                              "c04b05357c5d1c0eeac4c66f9ff7f2e6";

        using var cfb = new AesCfb(16);
        cfb.SetKey(key);
        cfb.Init();
        var buf = (byte[])pt.Clone();
        cfb.SetIV(iv);
        cfb.Encrypt(buf, 0, buf.Length);
        Assert.Equal(expect, Hex(buf));

        cfb.SetIV(iv);
        cfb.Decrypt(buf, 0, buf.Length);
        Assert.Equal(Hex(pt), Hex(buf));
    }

    [Fact]
    public void Cfb128_PartialLength_RoundTrips()
    {
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i + 3);
        var iv = new byte[16];
        for (int i = 0; i < iv.Length; i++) iv[i] = (byte)(255 - i);

        using var cfb = new AesCfb();
        cfb.SetKey(key);
        cfb.Init();

        var data = new byte[37]; // deliberately not a block multiple
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 5 + 1);
        var orig = (byte[])data.Clone();

        cfb.SetIV(iv);
        cfb.Encrypt(data, 0, data.Length);
        Assert.False(data.AsSpan().SequenceEqual(orig));
        cfb.SetIV(iv);
        cfb.Decrypt(data, 0, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(orig));
    }

    // -- EncryptedFileWithCache -----------------------------------------

    /// <summary>Minimal XTS layout over a raw base (data offset 0) for tests.</summary>
    private sealed class TestXtsLayout : IEncryptedFileLayout, IDisposable
    {
        private readonly AesXts _engine;

        public TestXtsLayout(byte[] key)
        {
            _engine = new AesXts();
            _engine.SetKey(key);
            _engine.Init();
        }

        public long EncryptedDataOffset => 0;
        public IFileEncryptionEngine Engine => _engine;

        public void SetEncryptionEngineIV(IFileEncryptionEngine eng, long decryptedVolumeOffset)
        {
            long block = (decryptedVolumeOffset + EncryptedDataOffset) / eng.FileBlockSize;
            var iv = new byte[eng.IVSize];
            BinaryPrimitives.WriteInt64BigEndian(iv, block);
            eng.SetIV(iv);
        }

        public void Dispose() => _engine.Dispose();
    }

    private static byte[] MakeKey() { var k = new byte[64]; for (int i = 0; i < 64; i++) k[i] = (byte)(i * 3 + 7); return k; }

    private static byte[] MakePayload(int n)
    {
        var p = new byte[n];
        for (int i = 0; i < n; i++) p[i] = (byte)((i * 131 + 17) & 0xff);
        return p;
    }

    // Snapshots the raw bytes currently held by an in-memory base IO. (Needed
    // because MemoryRandomAccessIO copies its seed array, so a second instance
    // built from the same seed would NOT see the writer's output.)
    private static byte[] Snapshot(MemoryRandomAccessIO io)
    {
        var buf = new byte[(int)io.Length()];
        io.Seek(0);
        ReadFull(io, buf);
        return buf;
    }

    [Fact]
    public void Cache_WrittenBytes_ReadBackByPlainEncryptedFile()
    {
        var key = MakeKey();
        // Small cache to force eviction: 2 buffers of 1 sector each.
        const int payloadLen = 512 * 20; // spans 20 buffers, forces eviction
        var payload = MakePayload(payloadLen);

        var baseIo = new MemoryRandomAccessIO(new byte[payloadLen]);
        using (var layout = new TestXtsLayout(key))
        using (var cached = new EncryptedFileWithCache(baseIo, layout, 2, 1))
        {
            cached.Seek(0);
            cached.Write(payload, 0, payload.Length);
            cached.Flush();
        }
        var cipher = Snapshot(baseIo);

        // Read the same ciphertext back through the plain (unbuffered) path.
        using (var baseIo2 = new MemoryRandomAccessIO(cipher))
        using (var layout = new TestXtsLayout(key))
        using (var plain = new EncryptedFile(baseIo2, layout))
        {
            var read = new byte[payloadLen];
            plain.Seek(0);
            int n = ReadFull(plain, read);
            Assert.Equal(payloadLen, n);
            Assert.True(read.AsSpan().SequenceEqual(payload));
        }
    }

    [Fact]
    public void Cache_ReadPath_MatchesPlainEncryptedFile()
    {
        var key = MakeKey();
        const int payloadLen = 512 * 20;
        var payload = MakePayload(payloadLen);

        var baseIo = new MemoryRandomAccessIO(new byte[payloadLen]);
        using (var layout = new TestXtsLayout(key))
        using (var plain = new EncryptedFile(baseIo, layout))
        {
            plain.Seek(0);
            plain.Write(payload, 0, payload.Length);
            plain.Flush();
        }
        var cipher = Snapshot(baseIo);

        // Random-access read via cache with tiny cache to exercise eviction.
        using (var baseIo2 = new MemoryRandomAccessIO(cipher))
        using (var layout = new TestXtsLayout(key))
        using (var cached = new EncryptedFileWithCache(baseIo2, layout, 3, 1))
        {
            var rnd = new Random(1234);
            for (int iter = 0; iter < 200; iter++)
            {
                int pos = rnd.Next(0, payloadLen);
                int len = rnd.Next(1, Math.Min(2000, payloadLen - pos) + 1);
                var chunk = new byte[len];
                cached.Seek(pos);
                int n = ReadFull(cached, chunk);
                Assert.Equal(len, n);
                Assert.True(chunk.AsSpan().SequenceEqual(payload.AsSpan(pos, len)),
                    $"mismatch at pos={pos} len={len}");
            }
        }
    }

    [Fact]
    public void Cache_ReadModifyWrite_AcrossBuffers()
    {
        var key = MakeKey();
        const int payloadLen = 512 * 10;
        var payload = MakePayload(payloadLen);
        var expected = (byte[])payload.Clone();

        var baseIo = new MemoryRandomAccessIO(new byte[payloadLen]);
        using (var layout = new TestXtsLayout(key))
        using (var cached = new EncryptedFileWithCache(baseIo, layout, 2, 1))
        {
            cached.Seek(0);
            cached.Write(payload, 0, payload.Length);
            cached.Flush();

            // overwrite a range straddling two 512-byte buffers
            var patch = MakePayload(300);
            for (int i = 0; i < patch.Length; i++) patch[i] ^= 0xA5;
            Array.Copy(patch, 0, expected, 400, patch.Length);
            cached.Seek(400);
            cached.Write(patch, 0, patch.Length);
            cached.Flush();
        }
        var cipher = Snapshot(baseIo);

        using (var baseIo2 = new MemoryRandomAccessIO(cipher))
        using (var layout = new TestXtsLayout(key))
        using (var plain = new EncryptedFile(baseIo2, layout))
        {
            var read = new byte[payloadLen];
            plain.Seek(0);
            ReadFull(plain, read);
            Assert.True(read.AsSpan().SequenceEqual(expected));
        }
    }

    // -- Encrypted streams ----------------------------------------------

    [Fact]
    public void EncryptedStreams_RoundTrip()
    {
        var key = MakeKey();
        const int payloadLen = 512 * 7; // whole-sector aligned
        var payload = MakePayload(payloadLen);

        byte[] cipherText;
        using (var layout = new TestXtsLayout(key))
        using (var mem = new MemoryStream())
        {
            using (var enc = new EncryptedOutputStream(mem, layout, ownsBase: false))
            {
                enc.Write(payload, 0, payload.Length);
                enc.Flush();
            }
            cipherText = mem.ToArray();
        }
        Assert.Equal(payloadLen, cipherText.Length);
        Assert.False(cipherText.AsSpan().SequenceEqual(payload));

        using (var layout = new TestXtsLayout(key))
        using (var mem = new MemoryStream(cipherText))
        using (var dec = new EncryptedInputStream(mem, layout, ownsBase: false))
        {
            var read = new byte[payloadLen];
            int total = 0, n;
            while (total < payloadLen && (n = dec.Read(read, total, payloadLen - total)) > 0) total += n;
            Assert.Equal(payloadLen, total);
            Assert.True(read.AsSpan().SequenceEqual(payload));
        }
    }

    private static int ReadFull(IRandomAccessIO io, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = io.Read(buf, total, buf.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>In-memory <see cref="IRandomAccessIO"/> for tests (grows on write).</summary>
public sealed class MemoryRandomAccessIO : IRandomAccessIO
{
    private byte[] _data;
    private long _length;
    private long _pos;

    public MemoryRandomAccessIO(byte[] initial)
    {
        _data = (byte[])initial.Clone();
        _length = initial.Length;
    }

    public void Seek(long position) => _pos = position;
    public long GetFilePointer() => _pos;
    public long Length() => _length;

    public int ReadByte()
    {
        if (_pos >= _length) return -1;
        return _data[_pos++];
    }

    public int Read(byte[] buffer, int offset, int len)
    {
        if (_pos >= _length) return -1;
        int n = (int)Math.Min(len, _length - _pos);
        Array.Copy(_data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public void WriteByte(int b) => Write(new[] { (byte)b }, 0, 1);

    public void Write(byte[] buffer, int offset, int len)
    {
        EnsureCapacity(_pos + len);
        Array.Copy(buffer, offset, _data, _pos, len);
        _pos += len;
        if (_pos > _length) _length = _pos;
    }

    public void Flush() { }

    public void SetLength(long newLength)
    {
        EnsureCapacity(newLength);
        _length = newLength;
        if (_pos > _length) _pos = _length;
    }

    private void EnsureCapacity(long need)
    {
        if (need <= _data.Length) return;
        long newCap = Math.Max(need, _data.Length * 2L);
        var grown = new byte[newCap];
        Array.Copy(_data, grown, _data.Length);
        _data = grown;
    }

    public void Dispose() { }
}
