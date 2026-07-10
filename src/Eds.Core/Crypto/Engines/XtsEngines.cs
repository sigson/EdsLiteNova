using Eds.Core.Crypto.BlockCiphers;
using Eds.Core.Crypto.Modes;

namespace Eds.Core.Crypto.Engines;

/// <summary>AES-XTS. Key 64 bytes (2x32). Mirrors <c>engines.AESXTS</c>.</summary>
public sealed class AesXts() : Xts(new Factory())
{
    private sealed class Factory : ICipherFactory
    {
        public int NumberOfCiphers => 1;
        public IBlockCipherNative CreateCipher(int typeIndex) => new Aes(32);
    }
    protected override int DefaultKeySize => 64;
    public override string CipherName => "AES";
}

/// <summary>Serpent-XTS. Key 64 bytes. Mirrors <c>engines.SerpentXTS</c>.</summary>
public sealed class SerpentXts() : Xts(new Factory())
{
    private sealed class Factory : ICipherFactory
    {
        public int NumberOfCiphers => 1;
        public IBlockCipherNative CreateCipher(int typeIndex) => new Serpent();
    }
    protected override int DefaultKeySize => 64;
    public override string CipherName => "Serpent";
}

/// <summary>Twofish-XTS. Key 64 bytes. Mirrors <c>engines.TwofishXTS</c>.</summary>
public sealed class TwofishXts() : Xts(new Factory())
{
    private sealed class Factory : ICipherFactory
    {
        public int NumberOfCiphers => 1;
        public IBlockCipherNative CreateCipher(int typeIndex) => new Twofish();
    }
    protected override int DefaultKeySize => 64;
    public override string CipherName => "Twofish";
}

/// <summary>GOST-XTS. Key 64 bytes. Mirrors <c>engines.GOSTXTS</c>.</summary>
public sealed class GostXts() : Xts(new Factory())
{
    private sealed class Factory : ICipherFactory
    {
        public int NumberOfCiphers => 1;
        public IBlockCipherNative CreateCipher(int typeIndex) => new Gost();
    }
    protected override int DefaultKeySize => 64;
    public override string CipherName => "GOST";
}
