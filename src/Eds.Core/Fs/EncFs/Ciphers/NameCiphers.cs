using System.Buffers.Binary;
using System.Text;
using Eds.Core.Crypto;
using Eds.Core.Fs.EncFs.Macs;

namespace Eds.Core.Fs.EncFs.Ciphers;

/// <summary>Identity name codec (no encryption). Port of <c>ciphers.NullNameCipher</c>.</summary>
public sealed class NullNameCipher : INameCodec
{
    public string EncodeName(string plaintextName) => plaintextName;
    public string DecodeName(string encodedName) => encodedName;
    public byte[]? GetChainedIV(string plaintextName) => null;
    public void Init(byte[] key) { }
    public void SetIV(byte[]? iv) { }
    public byte[]? GetIV() => null;
    public int IVSize => 0;
    public void Dispose() { }
}

/// <summary>
/// Block-mode filename cipher. Port of <c>ciphers.BlockNameCipher</c>. Pads the
/// name to the cipher block size, prepends a 16-bit MAC, encrypts with a
/// MAC-derived IV (XORed with the chained directory IV), then re-codes to base64
/// (or base32 when case-sensitive). Decoding verifies the MAC.
/// </summary>
public sealed class BlockNameCipher : INameCodec
{
    private readonly IFileEncryptionEngine _cipher;
    private readonly MacCalculator _hmac;
    private readonly bool _caseSensitive;
    private byte[]? _iv;
    private byte[]? _chainedIV;

    public BlockNameCipher(IFileEncryptionEngine cipher, MacCalculator mac, bool caseSensitive)
    {
        _cipher = cipher;
        _hmac = mac;
        _caseSensitive = caseSensitive;
    }

    public string EncodeName(string plaintextName)
    {
        byte[] plain = Encoding.UTF8.GetBytes(plaintextName);
        int len = plain.Length;
        int blockSize = _cipher.EncryptionBlockSize;
        int padding = blockSize - len % blockSize;
        if (padding == 0) padding = blockSize;

        var res = new byte[CalcEncodedLength(len + padding + 2)];
        Array.Copy(plain, 0, res, 2, len);
        for (int i = len + 2; i < len + padding + 2; i++) res[i] = (byte)padding;

        _hmac.SetChainedIV(_iv);
        short mac = _hmac.Calc16(res, 2, len + padding);
        _chainedIV = _hmac.GetChainedIV();
        BinaryPrimitives.WriteInt16BigEndian(res, mac);

        var iv = new byte[_cipher.IVSize];
        BinaryPrimitives.WriteInt64BigEndian(iv, mac & 0xFFFFL);
        XorChainedIV(iv);
        _cipher.SetIV(iv);
        _cipher.Encrypt(res, 2, len + padding);

        if (_caseSensitive)
        {
            B64.ChangeBase2Inline(res, 0, len + padding + 2, 8, 5, true);
            return B64.B32ToString(res, 0, res.Length);
        }
        B64.ChangeBase2Inline(res, 0, len + padding + 2, 8, 6, true);
        return B64.B64ToString(res, 0, res.Length);
    }

    public string DecodeName(string encodedName)
    {
        byte[] buf;
        if (_caseSensitive)
        {
            var tmp = B64.StringToB32(encodedName);
            buf = new byte[B64.B32ToB256Bytes(tmp.Length)];
            B64.ChangeBase2Inline(tmp, 0, tmp.Length, 5, 8, false, 0, 0, buf, 0);
        }
        else
        {
            var tmp = B64.StringToB64(encodedName);
            buf = new byte[B64.B64ToB256Bytes(tmp.Length)];
            B64.ChangeBase2Inline(tmp, 0, tmp.Length, 6, 8, false, 0, 0, buf, 0);
        }

        if (buf.Length - 2 < _cipher.EncryptionBlockSize)
            throw new ArgumentException("Encoded name is too short: " + encodedName);

        short mac = BinaryPrimitives.ReadInt16BigEndian(buf);
        var iv = new byte[_cipher.IVSize];
        BinaryPrimitives.WriteInt64BigEndian(iv, mac & 0xFFFFL);
        XorChainedIV(iv);
        _cipher.SetIV(iv);
        _cipher.Decrypt(buf, 2, buf.Length - 2);

        try
        {
            int padding = buf[buf.Length - 1];
            int finalSize = buf.Length - padding - 2;
            if (padding > _cipher.EncryptionBlockSize || finalSize < 0)
                throw new ArgumentException("Failed decoding name. Wrong padding. Name=" + encodedName);

            _hmac.SetChainedIV(_iv);
            short mac2 = _hmac.Calc16(buf, 2, buf.Length - 2);
            _chainedIV = _hmac.GetChainedIV();
            if (mac != mac2)
                throw new ArgumentException("Failed decoding name. Checksum mismatch. Name=" + encodedName);
            return Encoding.UTF8.GetString(buf, 2, finalSize);
        }
        finally { Array.Clear(buf); }
    }

    public void Init(byte[] key)
    {
        _cipher.SetKey(key);
        _cipher.Init();
        _hmac.Init(key);
    }

    public void SetIV(byte[]? iv) => _iv = iv;
    public byte[]? GetIV() => _iv;
    public int IVSize => 8;

    public byte[]? GetChainedIV(string plaintextName)
    {
        _chainedIV ??= CalcChainedIV(plaintextName);
        return _chainedIV;
    }

    public void Dispose()
    {
        _cipher.Dispose();
        _hmac.Dispose();
    }

    private void XorChainedIV(byte[] iv)
    {
        if (_iv != null)
            for (int i = 0; i < _iv.Length; i++) iv[i] ^= _iv[i];
    }

    private int CalcEncodedLength(int plainLength)
        => _caseSensitive ? B64.B256ToB32Bytes(plainLength) : B64.B256ToB64Bytes(plainLength);

    private byte[]? CalcChainedIV(string plaintextName)
    {
        byte[] plain = Encoding.UTF8.GetBytes(plaintextName);
        int len = plain.Length;
        int blockSize = _cipher.EncryptionBlockSize;
        int padding = blockSize - len % blockSize;
        if (padding == 0) padding = blockSize;
        var res = new byte[CalcEncodedLength(len + padding + 2)];
        Array.Copy(plain, 0, res, 2, len);
        for (int i = len + 2; i < len + padding + 2; i++) res[i] = (byte)padding;
        _hmac.SetChainedIV(_iv);
        _hmac.Calc64(res, 2, len + padding);
        return _hmac.GetChainedIV();
    }
}

/// <summary>
/// Stream-mode filename cipher. Port of <c>ciphers.StreamNameCipher</c>. Like the
/// block variant but with no padding: prepends a 16-bit MAC and stream-encrypts
/// the raw name with a MAC-derived IV, then base64-recodes.
/// </summary>
public sealed class StreamNameCipher : INameCodec
{
    private readonly IEncryptionEngine _cipher;
    private readonly MacCalculator _hmac;
    private byte[]? _iv;
    private byte[]? _chainedIV;

    public StreamNameCipher(IEncryptionEngine cipher, MacCalculator mac)
    {
        _cipher = cipher;
        _hmac = mac;
    }

    public string EncodeName(string plaintextName)
    {
        byte[] plain = Encoding.UTF8.GetBytes(plaintextName);
        int len = plain.Length;
        var res = new byte[B64.B256ToB64Bytes(len + 2)];
        Array.Copy(plain, 0, res, 2, len);

        _hmac.SetChainedIV(_iv);
        short mac = _hmac.Calc16(plain, 0, len);
        _chainedIV = _hmac.GetChainedIV();
        BinaryPrimitives.WriteInt16BigEndian(res, mac);

        var iv = new byte[_cipher.IVSize];
        BinaryPrimitives.WriteInt64BigEndian(iv, mac & 0xFFFFL);
        XorChainedIV(iv);
        _cipher.SetIV(iv);
        _cipher.Encrypt(res, 2, len);

        B64.ChangeBase2Inline(res, 0, len + 2, 8, 6, true);
        return B64.B64ToString(res, 0, res.Length);
    }

    public string DecodeName(string encodedName)
    {
        byte[] buf = B64.StringToB64(encodedName);
        int decodedLen = B64.B64ToB256Bytes(buf.Length) - 2;
        B64.ChangeBase2Inline(buf, 0, buf.Length, 6, 8, false);

        short mac = BinaryPrimitives.ReadInt16BigEndian(buf);
        var iv = new byte[_cipher.IVSize];
        BinaryPrimitives.WriteInt64BigEndian(iv, mac & 0xFFFFL);
        XorChainedIV(iv);
        _cipher.SetIV(iv);
        _cipher.Decrypt(buf, 2, decodedLen);

        try
        {
            _hmac.SetChainedIV(_iv);
            short mac2 = _hmac.Calc16(buf, 2, decodedLen);
            _chainedIV = _hmac.GetChainedIV();
            if (mac != mac2)
                throw new ArgumentException("Failed decoding name. Checksum mismatch. Name=" + encodedName);
            return Encoding.UTF8.GetString(buf, 2, decodedLen);
        }
        finally { Array.Clear(buf); }
    }

    public void Init(byte[] key)
    {
        _cipher.SetKey(key);
        _cipher.Init();
        _hmac.Init(key);
    }

    public void SetIV(byte[]? iv) => _iv = iv;
    public byte[]? GetIV() => _iv;
    public int IVSize => 8;

    public byte[]? GetChainedIV(string plaintextName)
    {
        _chainedIV ??= CalcChainedIV(plaintextName);
        return _chainedIV;
    }

    public void Dispose()
    {
        _cipher.Dispose();
        _hmac.Dispose();
    }

    private void XorChainedIV(byte[] iv)
    {
        if (_iv != null)
            for (int i = 0; i < _iv.Length; i++) iv[i] ^= _iv[i];
    }

    private byte[]? CalcChainedIV(string plaintextName)
    {
        byte[] plain = Encoding.UTF8.GetBytes(plaintextName);
        _hmac.SetChainedIV(_iv);
        _hmac.Calc64(plain, 0, plain.Length);
        return _hmac.GetChainedIV();
    }
}
