using System.Security.Cryptography;
using Eds.Core.Crypto;
using Eds.Core.Locations;

namespace Eds.Maui.Services;

/// <summary>
/// ⚠️ INSECURE placeholder. Persists a random 32-byte protection key in **plaintext**
/// MAUI <c>Preferences</c> and hands it back so saved per-location secrets can be
/// stored/read. This has NO real security — the key sits next to the data it
/// protects. It exists only to exercise the saved-secret path and MUST be replaced
/// with a platform secret-store provider (Android Keystore / iOS Keychain / Windows
/// DPAPI, e.g. over MAUI <c>SecureStorage</c>) before any real use.
/// </summary>
public sealed class SimpleStoreProtectionKeyProvider : IProtectionKeyProvider
{
    private const string PrefKey = "eds.protection_key_b64_INSECURE";

    public SecureBuffer? GetProtectionKey()
    {
        var b64 = Preferences.Default.Get(PrefKey, string.Empty);
        byte[] key;
        if (string.IsNullOrEmpty(b64))
        {
            key = new byte[32];
            RandomNumberGenerator.Fill(key);
            Preferences.Default.Set(PrefKey, Convert.ToBase64String(key));
        }
        else
        {
            key = Convert.FromBase64String(b64);
        }

        var buffer = new SecureBuffer(key);
        Array.Clear(key);
        return buffer;
    }
}
