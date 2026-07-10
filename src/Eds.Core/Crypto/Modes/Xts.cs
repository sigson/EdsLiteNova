using System.Buffers.Binary;
using Eds.Core.Crypto.Native;
using Eds.Core.Exceptions;

namespace Eds.Core.Crypto.Modes;

/// <summary>
/// XTS mode (xts-plain64). Faithful port of <c>crypto.modes.XTS</c>.
///
/// The managed layer only orchestrates: it creates the native XTS context,
/// builds block-cipher pairs from the <see cref="ICipherFactory"/>, splits the
/// key into data/tweak halves, initialises each cipher and attaches the native
/// pointers. All crypto math stays native.
///
/// Ownership: this class owns the block ciphers it creates and disposes them.
/// The native XTS context only owns the cipher-pair link nodes, not the ciphers.
/// </summary>
public abstract class Xts : IFileEncryptionEngine
{
    private const int SectorSize = 512;

    static Xts() => NativeLibraryResolver.EnsureRegistered();

    private sealed class CipherPair(IBlockCipherNative a, IBlockCipherNative b)
    {
        public readonly IBlockCipherNative CipherA = a;
        public readonly IBlockCipherNative CipherB = b;
    }

    private readonly ICipherFactory _cf;
    private readonly List<CipherPair> _blockCiphers = new();
    private nint _xtsContextPointer;
    private long _iv;
    private byte[]? _key;
    private bool _incrementIV;

    protected Xts(ICipherFactory cf) => _cf = cf;

    public int FileBlockSize => SectorSize;
    public int EncryptionBlockSize => 16;
    public int IVSize => 16;
    public string CipherModeName => "xts-plain64";
    public abstract string CipherName { get; }

    public int KeySize
    {
        get
        {
            int res = 0;
            foreach (var c in _blockCiphers) res += c.CipherA.KeySize;
            // When ciphers aren't built yet, defer to the concrete engine's default.
            return res == 0 ? DefaultKeySize : 2 * res;
        }
    }

    /// <summary>Key length before ciphers are instantiated (e.g. 64 for AES-XTS).</summary>
    protected abstract int DefaultKeySize { get; }

    public void Init()
    {
        CloseCiphers();
        CloseContext();

        _xtsContextPointer = NativeCrypto.XtsInit();
        if (_xtsContextPointer == nint.Zero)
            throw new EncryptionEngineException("XTS context initialization failed");

        AddBlockCiphers(_cf);

        if (_key == null)
            throw new EncryptionEngineException("Encryption key is not set");

        int keyOffset = 0;
        int eeKeySize = KeySize / 2;
        foreach (var p in _blockCiphers)
        {
            int ks = p.CipherA.KeySize;
            byte[] tmp = new byte[ks];
            try
            {
                Array.Copy(_key, keyOffset, tmp, 0, ks);
                p.CipherA.Init(tmp);
                Array.Copy(_key, eeKeySize + keyOffset, tmp, 0, ks);
                p.CipherB.Init(tmp);
                NativeCrypto.XtsAttach(_xtsContextPointer,
                    p.CipherA.NativeInterfacePointer,
                    p.CipherB.NativeInterfacePointer);
            }
            finally
            {
                Array.Clear(tmp);
            }
            keyOffset += ks;
        }
    }

    public void SetIV(byte[] iv)
    {
        // Original: ByteBuffer.wrap(iv).getLong() -> big-endian first 8 bytes.
        _iv = BinaryPrimitives.ReadInt64BigEndian(iv);
    }

    public byte[] GetIV()
    {
        var buf = new byte[IVSize];
        BinaryPrimitives.WriteInt64BigEndian(buf, _iv);
        return buf;
    }

    public void SetKey(byte[]? key)
    {
        ClearKey();
        _key = key == null ? null : ResizeCopy(key, KeySize);
    }

    public byte[]? GetKey() => _key;

    public void SetIncrementIV(bool value) => _incrementIV = value;

    public void Encrypt(byte[] data, int offset, int len)
    {
        if (_xtsContextPointer == nint.Zero) throw new EncryptionEngineException("Engine is closed");
        if (len % EncryptionBlockSize != 0 || offset + len > data.Length)
            throw new EncryptionEngineException("Wrong buffer length");
        if (NativeCrypto.XtsEncrypt(_xtsContextPointer, data, offset, len, (ulong)_iv) != 0)
            throw new EncryptionEngineException("Failed encrypting data");
        if (_incrementIV) _iv += len / FileBlockSize;
    }

    public void Decrypt(byte[] data, int offset, int len)
    {
        if (_xtsContextPointer == nint.Zero) throw new EncryptionEngineException("Engine is closed");
        if (len % EncryptionBlockSize != 0 || offset + len > data.Length)
            throw new EncryptionEngineException("Wrong buffer length");
        if (NativeCrypto.XtsDecrypt(_xtsContextPointer, data, offset, len, (ulong)_iv) != 0)
            throw new EncryptionEngineException("Failed decrypting data");
        if (_incrementIV) _iv += len / FileBlockSize;
    }

    public void Dispose()
    {
        CloseCiphers();
        CloseContext();
        ClearKey();
        GC.SuppressFinalize(this);
    }

    private void AddBlockCiphers(ICipherFactory cf)
    {
        for (int i = 0; i < cf.NumberOfCiphers; i++)
            _blockCiphers.Add(new CipherPair(cf.CreateCipher(i), cf.CreateCipher(i)));
    }

    private void CloseCiphers()
    {
        foreach (var p in _blockCiphers)
        {
            p.CipherA.Dispose();
            p.CipherB.Dispose();
        }
        _blockCiphers.Clear();
    }

    private void CloseContext()
    {
        if (_xtsContextPointer != nint.Zero)
        {
            NativeCrypto.XtsClose(_xtsContextPointer);
            _xtsContextPointer = nint.Zero;
        }
    }

    private void ClearKey()
    {
        if (_key != null)
        {
            Array.Clear(_key);
            _key = null;
        }
    }

    private static byte[] ResizeCopy(byte[] src, int length)
    {
        var dst = new byte[length];
        Array.Copy(src, dst, Math.Min(src.Length, length));
        return dst;
    }
}
