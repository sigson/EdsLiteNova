using Eds.Core.Crypto.Native;
using Eds.Core.Exceptions;

namespace Eds.Core.Crypto.Modes;

/// <summary>
/// CBC mode. Faithful port of <c>crypto.modes.CBC</c>. Like XTS, the managed
/// layer only orchestrates: it builds the cipher cascade, attaches native
/// pointers and forwards the 16-byte IV buffer (updated in place by the native
/// side). Used by LUKS1 and EncFS (with cipher-specific IV generation such as
/// plain64 or ESSIV supplied by the layout via <see cref="SetIV"/>).
/// </summary>
public abstract class Cbc : IFileEncryptionEngine
{
    protected const int CbcBlockSize = 16;

    static Cbc() => NativeLibraryResolver.EnsureRegistered();

    private readonly ICipherFactory _cf;
    private readonly List<IBlockCipherNative> _ciphers = new();
    private nint _ctx;
    private byte[]? _key;
    private readonly byte[] _iv = new byte[CbcBlockSize];
    private bool _incrementIV;

    protected Cbc(ICipherFactory cf, int fileBlockSize = 512)
    {
        _cf = cf;
        _fileBlockSize = fileBlockSize;
    }

    private readonly int _fileBlockSize;

    /// <summary>Sector size used when increment-IV is active (LUKS: 512);
    /// for EncFS this is set to the volume block size via the constructor.</summary>
    public virtual int FileBlockSize => _fileBlockSize;
    public int EncryptionBlockSize => CbcBlockSize;
    public int IVSize => CbcBlockSize;
    public abstract string CipherName { get; }
    public virtual string CipherModeName => "cbc-plain";

    protected abstract int DefaultKeySize { get; }

    public int KeySize
    {
        get
        {
            int res = 0;
            foreach (var c in _ciphers) res += c.KeySize;
            return res == 0 ? DefaultKeySize : res;
        }
    }

    public void Init()
    {
        CloseCiphers();
        CloseContext();

        _ctx = NativeCrypto.CbcInit(FileBlockSize);
        if (_ctx == nint.Zero) throw new EncryptionEngineException("CBC context initialization failed");

        for (int i = 0; i < _cf.NumberOfCiphers; i++) _ciphers.Add(_cf.CreateCipher(i));
        if (_key == null) throw new EncryptionEngineException("Encryption key is not set");

        int keyOffset = 0;
        foreach (var c in _ciphers)
        {
            int ks = c.KeySize;
            var tmp = new byte[ks];
            try
            {
                Array.Copy(_key, keyOffset, tmp, 0, ks);
                c.Init(tmp);
                NativeCrypto.CbcAttach(_ctx, c.NativeInterfacePointer);
            }
            finally { Array.Clear(tmp); }
            keyOffset += ks;
        }
    }

    public void SetIV(byte[] iv)
    {
        Array.Clear(_iv);
        Array.Copy(iv, _iv, Math.Min(iv.Length, _iv.Length));
    }

    public byte[] GetIV() => (byte[])_iv.Clone();

    public void SetKey(byte[]? key)
    {
        ClearKey();
        _key = key == null ? null : ResizeCopy(key, KeySize);
    }

    public byte[]? GetKey() => _key;
    public void SetIncrementIV(bool value) => _incrementIV = value;

    public void Encrypt(byte[] data, int offset, int len)
    {
        if (_ctx == nint.Zero) throw new EncryptionEngineException("Engine is closed");
        if (len % CbcBlockSize != 0 || offset + len > data.Length)
            throw new EncryptionEngineException("Wrong buffer length");
        var ivCopy = (byte[])_iv.Clone();
        if (NativeCrypto.CbcEncrypt(_ctx, data, offset, len, ivCopy, _incrementIV ? 1 : 0) != 0)
            throw new EncryptionEngineException("Failed encrypting data");
    }

    public void Decrypt(byte[] data, int offset, int len)
    {
        if (_ctx == nint.Zero) throw new EncryptionEngineException("Engine is closed");
        if (len % CbcBlockSize != 0 || offset + len > data.Length)
            throw new EncryptionEngineException("Wrong buffer length");
        var ivCopy = (byte[])_iv.Clone();
        if (NativeCrypto.CbcDecrypt(_ctx, data, offset, len, ivCopy, _incrementIV ? 1 : 0) != 0)
            throw new EncryptionEngineException("Failed decrypting data");
    }

    public void Dispose()
    {
        CloseCiphers();
        CloseContext();
        ClearKey();
        Array.Clear(_iv);
        GC.SuppressFinalize(this);
    }

    private void CloseCiphers()
    {
        foreach (var c in _ciphers) c.Dispose();
        _ciphers.Clear();
    }

    private void CloseContext()
    {
        if (_ctx != nint.Zero) { NativeCrypto.CbcClose(_ctx); _ctx = nint.Zero; }
    }

    private void ClearKey()
    {
        if (_key != null) { Array.Clear(_key); _key = null; }
    }

    private static byte[] ResizeCopy(byte[] src, int length)
    {
        var dst = new byte[length];
        Array.Copy(src, dst, Math.Min(src.Length, length));
        return dst;
    }
}
