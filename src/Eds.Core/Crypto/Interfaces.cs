namespace Eds.Core.Crypto;

/// <summary>
/// A symmetric encryption engine (cipher + mode). Mirrors
/// <c>com.sovworks.eds.crypto.EncryptionEngine</c>. <see cref="IDisposable"/>
/// replaces the Java <c>close()</c>.
/// </summary>
public interface IEncryptionEngine : IDisposable
{
    void Init();
    void Decrypt(byte[] data, int offset, int len);
    void Encrypt(byte[] data, int offset, int len);

    void SetIV(byte[] iv);
    byte[]? GetIV();
    int IVSize { get; }

    void SetKey(byte[]? key);
    byte[]? GetKey();
    int KeySize { get; }

    string CipherName { get; }
    string CipherModeName { get; }
}

/// <summary>
/// An engine that encrypts a file/volume sector-by-sector. Mirrors
/// <c>FileEncryptionEngine</c>.
/// </summary>
public interface IFileEncryptionEngine : IEncryptionEngine
{
    /// <summary>Sector size, e.g. 512 for XTS.</summary>
    int FileBlockSize { get; }

    /// <summary>Crypto block size, e.g. 16.</summary>
    int EncryptionBlockSize { get; }

    /// <summary>Whether the IV advances automatically per processed sector run.</summary>
    void SetIncrementIV(bool value);
}

/// <summary>A raw block cipher. Mirrors <c>BlockCipher</c>.</summary>
public interface IBlockCipher : IDisposable
{
    void Init(byte[] key);
    void EncryptBlock(byte[] data);
    void DecryptBlock(byte[] data);
    int KeySize { get; }
    int BlockSize { get; }
}

/// <summary>
/// A block cipher whose native context can be attached to a native mode.
/// Mirrors <c>BlockCipherNative.getNativeInterfacePointer()</c>.
/// </summary>
public interface IBlockCipherNative : IBlockCipher
{
    nint NativeInterfacePointer { get; }
}

/// <summary>Creates block ciphers for a mode. Mirrors <c>CipherFactory</c>.</summary>
public interface ICipherFactory
{
    IBlockCipherNative CreateCipher(int typeIndex);
    int NumberOfCiphers { get; }
}
