using Eds.Core.Crypto;
using Eds.Core.Crypto.Engines;

namespace Eds.Core.Fs.EncFs.Ciphers;

/// <summary>
/// AES-CBC file cipher for EncFS data blocks. Port of <c>ciphers.AESCBCFileCipher</c>.
/// Wraps a configurable-block <see cref="AesCbc"/> in the <see cref="CipherBase"/>
/// HMAC-IV derivation.
/// </summary>
public sealed class AesCbcFileCipher : CipherBase, IFileEncryptionEngine
{
    public AesCbcFileCipher(int keySize, int fileBlockSize)
        : base(new AesCbc(keySize, fileBlockSize)) { }

    private AesCbc B => (AesCbc)Base;

    public int FileBlockSize => B.FileBlockSize;
    public int EncryptionBlockSize => B.EncryptionBlockSize;
    public void SetIncrementIV(bool value) => B.SetIncrementIV(value);
}

/// <summary>
/// AES-CFB stream cipher for EncFS (filenames and final short block). Port of
/// <c>ciphers.AESCFBStreamCipher</c>.
/// </summary>
public sealed class AesCfbStreamCipher : StreamCipherBase
{
    public AesCfbStreamCipher(int keySize) : base(new AesCfb(keySize)) { }
}

/// <summary>
/// Dispatches whole-block operations to a block cipher and everything else (the
/// final short block) to a stream cipher. Port of <c>ciphers.BlockAndStreamCipher</c>.
/// This is the composite EncFS uses for file data: full blocks go through AES-CBC,
/// the trailing partial block through AES-CFB.
/// </summary>
public sealed class BlockAndStreamCipher : IFileEncryptionEngine
{
    private readonly IFileEncryptionEngine _block;
    private readonly IEncryptionEngine _stream;

    public BlockAndStreamCipher(IFileEncryptionEngine blockCipher, IEncryptionEngine streamCipher)
    {
        _block = blockCipher;
        _stream = streamCipher;
    }

    public int FileBlockSize => _block.FileBlockSize;
    public int EncryptionBlockSize => _block.EncryptionBlockSize;
    public void SetIncrementIV(bool value) => _block.SetIncrementIV(value);

    public void Init() { _block.Init(); _stream.Init(); }

    public void Decrypt(byte[] data, int offset, int len)
    {
        if (len == _block.FileBlockSize) _block.Decrypt(data, offset, len);
        else _stream.Decrypt(data, offset, len);
    }

    public void Encrypt(byte[] data, int offset, int len)
    {
        if (len == _block.FileBlockSize) _block.Encrypt(data, offset, len);
        else _stream.Encrypt(data, offset, len);
    }

    public void SetIV(byte[] iv) { _block.SetIV(iv); _stream.SetIV(iv); }
    public byte[]? GetIV() => _stream.GetIV();
    public int IVSize => _block.IVSize;

    public void SetKey(byte[]? key) { _block.SetKey(key); _stream.SetKey(key); }
    public byte[]? GetKey() => _block.GetKey();
    public int KeySize => _block.KeySize;

    public string CipherName => _block.CipherName;
    public string CipherModeName => _block.CipherModeName;

    public void Dispose() { _block.Dispose(); _stream.Dispose(); }
}
