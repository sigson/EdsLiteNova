using System.Buffers.Binary;
using Eds.Core.Crypto;
using Eds.Core.Crypto.Engines;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase C: the buffered EncryptedFile (per-buffer IV via the layout). Verifies
/// a buffered round-trip and that buffered ciphertext is byte-compatible with the
/// container (unbuffered) EncryptedFile for an XTS layout. See gap guide §4.3.
/// </summary>
public class BufferedEncryptedFileTests
{
    private sealed class XtsLayout : IEncryptedFileLayout, IDisposable
    {
        private readonly AesXts _engine;
        public XtsLayout(byte[] key) { _engine = new AesXts(); _engine.SetKey(key); _engine.Init(); }
        public long EncryptedDataOffset => 0;
        public IFileEncryptionEngine Engine => _engine;
        public void SetEncryptionEngineIV(IFileEncryptionEngine eng, long decryptedVolumeOffset)
        {
            long block = decryptedVolumeOffset / eng.FileBlockSize;
            var iv = new byte[eng.IVSize];
            BinaryPrimitives.WriteInt64BigEndian(iv, block);
            eng.SetIV(iv);
        }
        public void Dispose() => _engine.Dispose();
    }

    private static byte[] Key() { var k = new byte[64]; for (int i = 0; i < 64; i++) k[i] = (byte)(i * 5 + 1); return k; }
    private static byte[] Data(int n) { var d = new byte[n]; for (int i = 0; i < n; i++) d[i] = (byte)(i * 61 + 7); return d; }

    private static byte[] WriteBuffered(byte[] key, byte[] payload, int bufBlocks)
    {
        using var baseIo = new MemoryRandomAccessIO(Array.Empty<byte>());
        var bef = new BufferedEncryptedFile(baseIo, new XtsLayout(key), bufBlocks);
        bef.Seek(0);
        bef.Write(payload, 0, payload.Length);
        bef.Close(closeBase: false);
        var real = new byte[(int)baseIo.Length()];
        baseIo.Seek(0);
        int t = 0, n;
        while (t < real.Length && (n = baseIo.Read(real, t, real.Length - t)) > 0) t += n;
        return real;
    }

    [Fact]
    public void Buffered_RoundTrip()
    {
        var key = Key();
        var payload = Data(512 * 10);
        var cipher = WriteBuffered(key, payload, bufBlocks: 4);
        Assert.Equal(payload.Length, cipher.Length);
        Assert.False(cipher.AsSpan().SequenceEqual(payload));

        using var baseIo = new MemoryRandomAccessIO(cipher);
        using var bef = new BufferedEncryptedFile(baseIo, new XtsLayout(key), 4);
        var read = new byte[payload.Length];
        bef.Seek(0);
        int t = 0, n;
        while (t < read.Length && (n = bef.Read(read, t, read.Length - t)) > 0) t += n;
        Assert.Equal(payload.Length, t);
        Assert.True(read.AsSpan().SequenceEqual(payload));
    }

    [Fact]
    public void Buffered_Ciphertext_ReadableBy_Unbuffered()
    {
        var key = Key();
        var payload = Data(512 * 8);
        var cipher = WriteBuffered(key, payload, bufBlocks: 4);

        // The container (unbuffered) EncryptedFile must decode the same bytes.
        using var baseIo = new MemoryRandomAccessIO(cipher);
        using var ef = new EncryptedFile(baseIo, new XtsLayout(key));
        var read = new byte[payload.Length];
        ef.Seek(0);
        int t = 0, n;
        while (t < read.Length && (n = ef.Read(read, t, read.Length - t)) > 0) t += n;
        Assert.Equal(payload.Length, t);
        Assert.True(read.AsSpan().SequenceEqual(payload));
    }
}
