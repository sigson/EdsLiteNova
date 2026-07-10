using System.Buffers.Binary;
using Eds.Core.Crypto;
using Eds.Core.Crypto.Hash;
using Eds.Core.Crypto.Kdf;

namespace Eds.Core.Fs.EncFs.Ciphers;

/// <summary>
/// EncFS cipher wrapper. Faithful port of <c>fs.encfs.ciphers.CipherBase</c>.
/// Wraps a base <see cref="IEncryptionEngine"/> and derives its per-operation IV
/// from an 8-byte "file IV" via HMAC-SHA1 over (ivPart || reversed fileIV). The
/// combined key is (baseKey || ivPart); ivPart feeds the HMAC. This IV
/// derivation is central to EncFS compatibility and is reproduced exactly.
/// </summary>
public class CipherBase : IEncryptionEngine
{
    private readonly IEncryptionEngine _base;
    private byte[]? _key, _keyPart, _ivPart;
    private Hmac? _hmac;

    public CipherBase(IEncryptionEngine baseEngine) => _base = baseEngine;

    protected IEncryptionEngine Base => _base;

    public static byte[] GetKeyFromBuf(byte[] buf, int keySize)
    {
        var res = new byte[keySize];
        Array.Copy(buf, 0, res, 0, keySize);
        return res;
    }

    private static byte[] GetIVFromBuf(byte[] buf, int keySize)
    {
        var res = new byte[buf.Length - keySize];
        Array.Copy(buf, keySize, res, 0, res.Length);
        return res;
    }

    public virtual void Init()
    {
        ClearHmac();
        if (_keyPart == null) throw new InvalidOperationException("Cipher key is not set");
        _hmac = new Hmac(_keyPart, BclDigest.Sha1(), 64);
        _base.Init();
    }

    public virtual void Decrypt(byte[] data, int offset, int len) => _base.Decrypt(data, offset, len);
    public virtual void Encrypt(byte[] data, int offset, int len) => _base.Encrypt(data, offset, len);

    public virtual void SetIV(byte[] iv)
    {
        if (_ivPart == null || _hmac == null) throw new InvalidOperationException("Cipher is not initialized");
        var buf = new byte[_ivPart.Length + 8];
        Array.Copy(_ivPart, buf, _ivPart.Length);
        for (int i = 0; i < 8; i++) buf[_ivPart.Length + i] = iv[7 - i];
        try
        {
            var hmac = new byte[_hmac.DigestLength];
            _hmac.CalcHmac(buf, 0, buf.Length, hmac);
            var baseIv = new byte[_base.IVSize];
            Array.Copy(hmac, 0, baseIv, 0, baseIv.Length);
            _base.SetIV(baseIv);
        }
        finally { Array.Clear(buf); }
    }

    public byte[]? GetIV() => _key == null ? null : GetIVFromBuf(_key, _base.KeySize);
    public int IVSize => _base.IVSize;

    public void SetKey(byte[]? key)
    {
        ClearKey();
        if (key != null)
        {
            _key = ResizeCopy(key, KeySize);
            _keyPart = GetKeyFromBuf(_key, _base.KeySize);
            _ivPart = GetIVFromBuf(_key, _base.KeySize);
            _base.SetKey(_keyPart);
        }
    }

    public byte[]? GetKey() => _key;
    public int KeySize => _base.KeySize + IVSize;

    public string CipherName => _base.CipherName;
    public string CipherModeName => _base.CipherModeName;

    public virtual void Dispose()
    {
        ClearKey();
        ClearHmac();
        _base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ClearHmac()
    {
        _hmac?.Dispose();
        _hmac = null;
    }

    private void ClearKey()
    {
        if (_key != null)
        {
            Array.Clear(_key);
            if (_ivPart != null) Array.Clear(_ivPart);
            if (_keyPart != null) Array.Clear(_keyPart);
            _key = _ivPart = _keyPart = null;
        }
    }

    private static byte[] ResizeCopy(byte[] src, int length)
    {
        var dst = new byte[length];
        Array.Copy(src, dst, Math.Min(src.Length, length));
        return dst;
    }
}

/// <summary>
/// EncFS stream cipher. Faithful port of <c>ciphers.StreamCipherBase</c>. Adds the
/// characteristic EncFS two-pass stream encoding around the base cipher: shuffle
/// → encrypt(iv) → flip → shuffle → encrypt(iv+1), and the inverse on decrypt.
/// The 8-byte file IV is stored as a big-endian long. Used for filenames and the
/// final (short) block of a file.
/// </summary>
public class StreamCipherBase : CipherBase
{
    private long _ivLong;

    public StreamCipherBase(IEncryptionEngine baseEngine) : base(baseEngine) { }

    public override void SetIV(byte[] iv)
        => _ivLong = iv == null ? 0 : BinaryPrimitives.ReadInt64BigEndian(iv);

    public override void Encrypt(byte[] data, int offset, int len)
    {
        ShuffleBytes(data, offset, len);
        var iv = new byte[IVSize];
        BinaryPrimitives.WriteInt64BigEndian(iv, _ivLong);
        base.SetIV(iv);
        base.Encrypt(data, offset, len);
        FlipBytes(data, offset, len);
        ShuffleBytes(data, offset, len);
        BinaryPrimitives.WriteInt64BigEndian(iv, _ivLong + 1);
        base.SetIV(iv);
        base.Encrypt(data, offset, len);
    }

    public override void Decrypt(byte[] data, int offset, int len)
    {
        var iv = new byte[IVSize];
        BinaryPrimitives.WriteInt64BigEndian(iv, _ivLong + 1);
        base.SetIV(iv);
        base.Decrypt(data, offset, len);
        UnshuffleBytes(data, offset, len);
        FlipBytes(data, offset, len);
        BinaryPrimitives.WriteInt64BigEndian(iv, _ivLong);
        base.SetIV(iv);
        base.Decrypt(data, offset, len);
        UnshuffleBytes(data, offset, len);
    }

    private static void ShuffleBytes(byte[] buf, int offset, int count)
    {
        for (int i = 0; i < count - 1; ++i) buf[i + offset + 1] ^= buf[i + offset];
    }

    private static void UnshuffleBytes(byte[] buf, int offset, int count)
    {
        for (int i = count - 1; i > 0; --i) buf[i + offset] ^= buf[i + offset - 1];
    }

    private static void FlipBytes(byte[] buf, int offset, int count)
    {
        var revBuf = new byte[64];
        int bytesLeft = count;
        while (bytesLeft > 0)
        {
            int toFlip = Math.Min(revBuf.Length, bytesLeft);
            for (int i = 0; i < toFlip; ++i) revBuf[i] = buf[toFlip + offset - (i + 1)];
            Array.Copy(revBuf, 0, buf, offset, toFlip);
            bytesLeft -= toFlip;
            offset += toFlip;
        }
        Array.Clear(revBuf);
    }
}
