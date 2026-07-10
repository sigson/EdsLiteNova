using System.Buffers.Binary;
using Eds.Core.Crypto.Hash;
using Eds.Core.Crypto.Kdf;

namespace Eds.Core.Fs.EncFs.Macs;

/// <summary>
/// Block/name integrity MAC for EncFS. Faithful port of
/// <c>fs.encfs.macs.MACCalculator</c>. Produces an 8-byte checksum which is
/// folded down to 32 or 16 bits for the on-disk MAC headers. Optionally chains
/// the previous MAC as an IV (EncFS "chainedIV" option).
/// </summary>
public abstract class MacCalculator : IDisposable
{
    private byte[]? _chainedIV;
    private bool _useChainedIV;

    public void SetChainedIV(byte[]? iv)
    {
        _chainedIV = iv;
        _useChainedIV = iv != null;
    }

    public bool IsChainedIVEnabled => _useChainedIV;
    public byte[]? GetChainedIV() => _chainedIV;

    public virtual void Init(byte[] key) { }

    public long Calc64(byte[] buf, int offset, int count)
        => BinaryPrimitives.ReadInt64BigEndian(CalcChecksum(buf, offset, count));

    public int Calc32(byte[] buf, int offset, int count)
    {
        var cs = CalcChecksum(buf, offset, count);
        for (int i = 0; i < 4; i++) cs[i] ^= cs[i + 4];
        return BinaryPrimitives.ReadInt32BigEndian(cs);
    }

    public short Calc16(byte[] buf, int offset, int count)
    {
        var cs = CalcChecksum(buf, offset, count);
        for (int i = 0; i < 4; i++) cs[i] ^= cs[i + 4];
        for (int i = 0; i < 2; i++) cs[i] ^= cs[i + 2];
        return BinaryPrimitives.ReadInt16BigEndian(cs);
    }

    /// <summary>Returns the raw 8-byte checksum for the given data.</summary>
    public abstract byte[] CalcChecksum(byte[] buf, int offset, int count);

    public virtual void Dispose() { }
}

/// <summary>
/// HMAC-SHA1 based MAC, folded to 8 bytes. Faithful port of
/// <c>fs.encfs.macs.SHA1MACCalculator</c>: HMAC-SHA1 over the data (with the
/// reversed chained IV appended when enabled), then the first 19 bytes of the
/// 20-byte HMAC are XOR-folded into 8 bytes. Byte-for-byte compatibility with
/// desktop EncFS is required, so the odd 19-byte fold is reproduced exactly.
/// </summary>
public sealed class Sha1MacCalculator : MacCalculator
{
    private readonly int _keySize;
    private Hmac? _hmac;

    public Sha1MacCalculator(int keySize) => _keySize = keySize;

    public override void Init(byte[] key)
    {
        var k = GetKeyFromBuf(key, _keySize);
        try
        {
            // SHA-1: 64-byte block, 20-byte digest.
            _hmac = new Hmac(k, BclDigest.Sha1(), 64);
        }
        finally { Array.Clear(k); }
    }

    public override byte[] CalcChecksum(byte[] buf, int offset, int count)
    {
        if (_hmac == null) throw new InvalidOperationException("MAC calculator is not initialized");

        byte[] data;
        if (IsChainedIVEnabled)
        {
            var iv = GetChainedIV()!;
            data = new byte[count + 8];
            Array.Copy(buf, offset, data, 0, count);
            for (int i = 0; i < 8; i++) data[count + i] = iv[7 - i];
        }
        else
        {
            data = new byte[count];
            Array.Copy(buf, offset, data, 0, count);
        }

        try
        {
            var mac = new byte[_hmac.DigestLength]; // 20
            _hmac.CalcHmac(data, 0, data.Length, mac);
            var cut = new byte[8];
            for (int i = 0; i < mac.Length - 1; i++) // note: original folds mac[0..18], skipping the last byte
                cut[i % cut.Length] ^= mac[i];
            if (IsChainedIVEnabled) SetChainedIV((byte[])cut.Clone());
            return cut;
        }
        finally { Array.Clear(data); }
    }

    public override void Dispose()
    {
        _hmac?.Dispose();
        _hmac = null;
    }

    /// <summary>Mirrors <c>CipherBase.getKeyFromBuf</c>: the first keySize bytes.</summary>
    private static byte[] GetKeyFromBuf(byte[] buf, int keySize)
    {
        var res = new byte[keySize];
        Array.Copy(buf, 0, res, 0, keySize);
        return res;
    }
}
