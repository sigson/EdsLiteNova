using System.Security.Cryptography;
using Eds.Core.Crypto;
using Eds.Core.Locations;

namespace Eds.Maui.Services;

/// <summary>
/// Protection-key provider backed by the platform secret store via MAUI
/// <see cref="SecureStorage"/> — Android Keystore, iOS/macOS Keychain, Windows
/// DPAPI (Data Protection). The 32-byte key that wraps saved per-location secrets
/// is generated once and kept in the OS-managed secure store, never beside the
/// data it protects. This replaces <see cref="SimpleStoreProtectionKeyProvider"/>.
///
/// <para><b>Threading.</b> <see cref="SecureStorage"/> is async-only, so the key is
/// fetched synchronously here via <see cref="Task.GetAwaiter"/>. The provider is
/// only consulted off the UI thread (during open/save, from the controller), so
/// this cannot deadlock the UI; the value is cached after first use.</para>
///
/// <para><b>Fallback.</b> On the rare platform/OS state where SecureStorage is
/// unavailable (e.g. no keystore on some emulators, or a thrown
/// <see cref="PlatformNotSupportedException"/>), it degrades to the insecure
/// Preferences provider so the saved-secret feature still functions, and surfaces
/// that via <see cref="IsSecure"/> for the UI to warn on.</para>
/// </summary>
public sealed class SecureStoreProtectionKeyProvider : IProtectionKeyProvider
{
    private const string StoreKey = "eds.protection_key_v1";

    private readonly object _lock = new();
    private readonly SimpleStoreProtectionKeyProvider _fallback = new();
    private byte[]? _cached;
    private bool _resolved;

    /// <summary>True once a key has been served from the real secret store.</summary>
    public bool IsSecure { get; private set; }

    public SecureBuffer? GetProtectionKey()
    {
        lock (_lock)
        {
            if (_resolved && _cached != null)
                return CopyToBuffer(_cached);

            try
            {
                byte[] key = LoadOrCreateAsync().GetAwaiter().GetResult();
                _cached = key;
                _resolved = true;
                IsSecure = true;
                return CopyToBuffer(key);
            }
            catch (Exception ex) when (
                ex is PlatformNotSupportedException or NotSupportedException ||
                ex.GetType().Name.Contains("SecureStorage", StringComparison.Ordinal))
            {
                // Secret store unavailable on this device/state — degrade, but flag it.
                IsSecure = false;
                _resolved = true;
                return _fallback.GetProtectionKey();
            }
        }
    }

    private static async Task<byte[]> LoadOrCreateAsync()
    {
        string? existing = await SecureStorage.Default.GetAsync(StoreKey).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existing))
            return Convert.FromBase64String(existing);

        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        await SecureStorage.Default.SetAsync(StoreKey, Convert.ToBase64String(key)).ConfigureAwait(false);
        return key;
    }

    private static SecureBuffer CopyToBuffer(byte[] key)
    {
        // Hand out a copy so the caller can dispose/erase without clearing our cache.
        var copy = (byte[])key.Clone();
        var buffer = new SecureBuffer(copy);
        Array.Clear(copy);
        return buffer;
    }
}
