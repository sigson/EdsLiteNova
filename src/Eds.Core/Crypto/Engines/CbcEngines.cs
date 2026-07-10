using Eds.Core.Crypto.BlockCiphers;
using Eds.Core.Crypto.Modes;

namespace Eds.Core.Crypto.Engines;

/// <summary>
/// AES-CBC (single cipher). Used by LUKS1 (default 32-byte key, 512 block) and
/// by EncFS (configurable key size and volume block size via the second ctor).
/// </summary>
public sealed class AesCbc : Cbc
{
    private readonly int _keySize;

    public AesCbc() : this(32, 512) { }

    public AesCbc(int keySize, int fileBlockSize) : base(new Factory(keySize), fileBlockSize)
        => _keySize = keySize;

    private sealed class Factory(int keySize) : ICipherFactory
    {
        public int NumberOfCiphers => 1;
        public IBlockCipherNative CreateCipher(int typeIndex) => new Aes(keySize);
    }

    protected override int DefaultKeySize => _keySize;
    public override string CipherName => "AES";
}

/// <summary>Serpent-CBC (single cipher, 32-byte key).</summary>
public sealed class SerpentCbc() : Cbc(new Factory())
{
    private sealed class Factory : ICipherFactory
    {
        public int NumberOfCiphers => 1;
        public IBlockCipherNative CreateCipher(int typeIndex) => new Serpent();
    }
    protected override int DefaultKeySize => 32;
    public override string CipherName => "Serpent";
}

/// <summary>Twofish-CBC (single cipher, 32-byte key).</summary>
public sealed class TwofishCbc() : Cbc(new Factory())
{
    private sealed class Factory : ICipherFactory
    {
        public int NumberOfCiphers => 1;
        public IBlockCipherNative CreateCipher(int typeIndex) => new Twofish();
    }
    protected override int DefaultKeySize => 32;
    public override string CipherName => "Twofish";
}

/// <summary>
/// GOST-CBC (single cipher, 32-byte key). Mirrors <c>engines.GOSTCBC</c>.
/// The CBC chaining runs at a fixed 16-byte crypto-block granularity exactly
/// as in the original (which also hardcodes <c>getEncryptionBlockSize()==16</c>
/// regardless of the 8-byte GOST block), so containers created by edslite with
/// GOST-CBC decrypt byte-for-byte identically.
/// </summary>
public sealed class GostCbc() : Cbc(new Factory())
{
    private sealed class Factory : ICipherFactory
    {
        public int NumberOfCiphers => 1;
        public IBlockCipherNative CreateCipher(int typeIndex) => new Gost();
    }
    protected override int DefaultKeySize => 32;
    public override string CipherName => "GOST";
}
