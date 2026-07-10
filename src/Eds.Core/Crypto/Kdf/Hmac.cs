using Eds.Core.Crypto.Hash;

namespace Eds.Core.Crypto.Kdf;

/// <summary>
/// HMAC computed manually over an <see cref="IMessageDigest"/>. Faithful port of
/// <c>crypto.kdf.HMAC</c>. A manual implementation is required because the custom
/// digests (RIPEMD-160, Whirlpool) are not available through the BCL HMAC types.
/// </summary>
public sealed class Hmac : IDisposable
{
    private readonly IMessageDigest _md;
    private readonly byte[] _digest;
    private readonly byte[] _block;
    private readonly byte[] _key;

    public Hmac(byte[] key, IMessageDigest md, int blockSize)
    {
        _md = md;
        _digest = new byte[DigestLength];
        _block = new byte[blockSize];
        _key = key.Length > _block.Length ? md.DoFinal(key) : (byte[])key.Clone();
    }

    public int DigestLength => _md.DigestLength;

    public void CalcHmac(byte[] data, int dataOffset, int dataLen, byte[] output)
    {
        _md.Reset();
        for (int i = 0; i < _key.Length; i++) _block[i] = (byte)(_key[i] ^ 0x36);
        for (int i = _key.Length; i < _block.Length; i++) _block[i] = 0x36;

        _md.Update(_block);
        _md.Update(data, dataOffset, dataLen);
        _md.DoFinal(_digest, 0);

        for (int i = 0; i < _key.Length; i++) _block[i] = (byte)(_key[i] ^ 0x5C);
        for (int i = _key.Length; i < _block.Length; i++) _block[i] = 0x5C;
        _md.Update(_block);
        _md.Update(_digest);
        _md.DoFinal(_digest, 0);

        Array.Copy(_digest, 0, output, 0, _digest.Length);
    }

    public void Dispose()
    {
        _md.Reset();
        Array.Clear(_key);
        Array.Clear(_digest);
        Array.Clear(_block);
    }
}
