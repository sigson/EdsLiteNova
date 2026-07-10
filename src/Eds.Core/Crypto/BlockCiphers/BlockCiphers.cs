using Eds.Core.Crypto.Native;
using Eds.Core.Exceptions;

namespace Eds.Core.Crypto.BlockCiphers;

/// <summary>
/// Base for the native block-cipher wrappers. Holds the opaque native context
/// (<c>block_cipher_interface*</c>) and guarantees release via <see cref="Dispose"/>.
/// Mirrors the pattern of <c>crypto.blockciphers.AES</c> etc.
/// </summary>
public abstract class NativeBlockCipherBase : IBlockCipherNative
{
    static NativeBlockCipherBase() => NativeLibraryResolver.EnsureRegistered();

    protected nint ContextPtr;

    protected NativeBlockCipherBase(int keySize) => KeySize = keySize;

    public int KeySize { get; }
    public abstract int BlockSize { get; }

    public void Init(byte[] key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException(
                $"Wrong key length. Required: {KeySize}. Provided: {key.Length}");
        ContextPtr = InitNative(key);
        if (ContextPtr == nint.Zero)
            throw new EncryptionEngineException($"{GetType().Name} context initialization failed");
    }

    public void EncryptBlock(byte[] data)
    {
        if (ContextPtr == nint.Zero) throw new EncryptionEngineException("Cipher is closed");
        EncryptNative(ContextPtr, data);
    }

    public void DecryptBlock(byte[] data)
    {
        if (ContextPtr == nint.Zero) throw new EncryptionEngineException("Cipher is closed");
        DecryptNative(ContextPtr, data);
    }

    /// <summary>Pointer handed to a native mode via attach. Mirrors getNativeInterfacePointer().</summary>
    public nint NativeInterfacePointer => ContextPtr;

    protected abstract nint InitNative(byte[] key);
    protected abstract void EncryptNative(nint ctx, byte[] block);
    protected abstract void DecryptNative(nint ctx, byte[] block);
    protected abstract void CloseNative(nint ctx);

    public void Dispose()
    {
        if (ContextPtr != nint.Zero)
        {
            CloseNative(ContextPtr);
            ContextPtr = nint.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~NativeBlockCipherBase() => Dispose();
}

/// <summary>AES block cipher (block 16; key 16/24/32). Default key 32.</summary>
public sealed class Aes : NativeBlockCipherBase
{
    public Aes() : this(32) { }
    public Aes(int keySize) : base(keySize) { }
    public override int BlockSize => 16;
    protected override nint InitNative(byte[] key) => NativeCrypto.AesInit(key, key.Length);
    protected override void EncryptNative(nint ctx, byte[] block) => NativeCrypto.AesEncrypt(ctx, block);
    protected override void DecryptNative(nint ctx, byte[] block) => NativeCrypto.AesDecrypt(ctx, block);
    protected override void CloseNative(nint ctx) => NativeCrypto.AesClose(ctx);
}

/// <summary>Serpent block cipher (block 16; key 32).</summary>
public sealed class Serpent : NativeBlockCipherBase
{
    public Serpent() : base(32) { }
    public override int BlockSize => 16;
    protected override nint InitNative(byte[] key) => NativeCrypto.SerpentInit(key, key.Length);
    protected override void EncryptNative(nint ctx, byte[] block) => NativeCrypto.SerpentEncrypt(ctx, block);
    protected override void DecryptNative(nint ctx, byte[] block) => NativeCrypto.SerpentDecrypt(ctx, block);
    protected override void CloseNative(nint ctx) => NativeCrypto.SerpentClose(ctx);
}

/// <summary>Twofish block cipher (block 16; key 32).</summary>
public sealed class Twofish : NativeBlockCipherBase
{
    public Twofish() : base(32) { }
    public override int BlockSize => 16;
    protected override nint InitNative(byte[] key) => NativeCrypto.TwofishInit(key, key.Length);
    protected override void EncryptNative(nint ctx, byte[] block) => NativeCrypto.TwofishEncrypt(ctx, block);
    protected override void DecryptNative(nint ctx, byte[] block) => NativeCrypto.TwofishDecrypt(ctx, block);
    protected override void CloseNative(nint ctx) => NativeCrypto.TwofishClose(ctx);
}

/// <summary>
/// GOST 28147-89 block cipher (block 8; key 32). <see cref="UseTestSubstMask"/>
/// selects the test S-box param set (matches the original _useTestSubstMask);
/// standard containers keep it false.
/// </summary>
public sealed class Gost : NativeBlockCipherBase
{
    public Gost() : base(32) { }
    public override int BlockSize => 8;
    public bool UseTestSubstMask { get; set; }
    protected override nint InitNative(byte[] key) => NativeCrypto.GostInit(key, key.Length, UseTestSubstMask ? 1 : 0);
    protected override void EncryptNative(nint ctx, byte[] block) => NativeCrypto.GostEncrypt(ctx, block);
    protected override void DecryptNative(nint ctx, byte[] block) => NativeCrypto.GostDecrypt(ctx, block);
    protected override void CloseNative(nint ctx) => NativeCrypto.GostClose(ctx);
}
