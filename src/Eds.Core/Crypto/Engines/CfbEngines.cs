using Eds.Core.Crypto.BlockCiphers;
using Eds.Core.Crypto.Modes;

namespace Eds.Core.Crypto.Engines;

/// <summary>
/// AES-CFB (single AES cipher). Mirrors <c>engines.AESCFB</c>. The key size
/// (16/24/32) is chosen at construction; EncFS builds it from the volume key
/// length declared in <c>encfs6.xml</c>.
/// </summary>
public sealed class AesCfb : Cfb
{
    private readonly int _keySize;

    public AesCfb() : this(32) { }

    public AesCfb(int keySize) : base(new Factory(keySize)) => _keySize = keySize;

    private sealed class Factory(int keySize) : ICipherFactory
    {
        public int NumberOfCiphers => 1;
        public IBlockCipherNative CreateCipher(int typeIndex) => new Aes(keySize);
    }

    protected override int DefaultKeySize => _keySize;
    public override string CipherName => "AES";
}
