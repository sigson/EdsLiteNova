using Eds.Core.Crypto.Native;
using Eds.Core.Exceptions;

namespace Eds.Core.Crypto.Modes;

/// <summary>
/// CFB-128 mode (full-block feedback). Faithful port of <c>crypto.modes.CFB</c>.
/// Unlike XTS/CBC this is a plain <see cref="IEncryptionEngine"/> (not a
/// <see cref="IFileEncryptionEngine"/>): there is no sector/file-block notion,
/// the whole buffer is a single self-synchronising stream keyed by the 16-byte
/// IV. Used by EncFS <c>AESCFBStreamCipher</c> for file-name and stream-tail
/// encryption.
///
/// Like the other modes the managed layer only orchestrates: it builds the
/// cipher cascade, attaches the native pointers and forwards the IV buffer
/// (the native side updates it in place with the running feedback). All crypto
/// math stays native, so ciphertext is byte-for-byte identical to the original.
/// </summary>
public abstract class Cfb : IEncryptionEngine
{
    protected const int CfbBlockSize = 16;

    static Cfb() => NativeLibraryResolver.EnsureRegistered();

    private readonly ICipherFactory _cf;
    private readonly List<IBlockCipherNative> _ciphers = new();
    private nint _ctx;
    private byte[]? _key;
    private byte[] _iv = new byte[CfbBlockSize];

    protected Cfb(ICipherFactory cf) => _cf = cf;

    public int IVSize => CfbBlockSize;
    public abstract string CipherName { get; }
    public string CipherModeName => "cfb-plain";

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

        _ctx = NativeCrypto.CfbInit();
        if (_ctx == nint.Zero) throw new EncryptionEngineException("CFB context initialization failed");

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
                NativeCrypto.CfbAttach(_ctx, c.NativeInterfacePointer);
            }
            finally { Array.Clear(tmp); }
            keyOffset += ks;
        }
    }

    public void SetIV(byte[] iv)
    {
        // Original keeps a direct reference; we copy into a fixed 16-byte buffer.
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

    public void Encrypt(byte[] data, int offset, int len)
    {
        if (_ctx == nint.Zero) throw new EncryptionEngineException("Engine is closed");
        if (len == 0) return;
        if (offset + len > data.Length)
            throw new ArgumentException("Wrong length or offset");
        // Native updates the IV in place with the running feedback, matching the
        // original (whose _iv reference is likewise advanced across calls).
        if (NativeCrypto.CfbEncrypt(_ctx, data, offset, len, _iv) != 0)
            throw new EncryptionEngineException("Failed encrypting data");
    }

    public void Decrypt(byte[] data, int offset, int len)
    {
        if (_ctx == nint.Zero) throw new EncryptionEngineException("Engine is closed");
        if (len == 0) return;
        if (offset + len > data.Length)
            throw new ArgumentException("Wrong length or offset");
        if (NativeCrypto.CfbDecrypt(_ctx, data, offset, len, _iv) != 0)
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
        if (_ctx != nint.Zero) { NativeCrypto.CfbClose(_ctx); _ctx = nint.Zero; }
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
