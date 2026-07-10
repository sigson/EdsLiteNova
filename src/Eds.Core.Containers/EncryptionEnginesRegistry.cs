using Eds.Core.Crypto;
using Eds.Core.Crypto.Engines;

namespace Eds.Core.Containers;

/// <summary>
/// Supported engines for TrueCrypt/VeraCrypt standard layouts. Mirrors
/// <c>truecrypt.EncryptionEnginesRegistry</c> (AES, Serpent, Twofish - all XTS).
/// </summary>
public static class EncryptionEnginesRegistry
{
    public static List<IFileEncryptionEngine> GetSupportedEncryptionEngines() => new()
    {
        new AesXts(),
        new SerpentXts(),
        new TwofishXts(),
    };

    public static string GetEncEngineName(IEncryptionEngine eng) => eng.CipherName;
}
