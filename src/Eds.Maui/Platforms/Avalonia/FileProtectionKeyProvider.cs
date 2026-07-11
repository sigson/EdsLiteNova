using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Eds.Core.Crypto;
using Eds.Core.Locations;

namespace Eds.Maui.Services;

/// <summary>
/// Protection-key provider for the Avalonia (net11.0) head, where the preview MAUI
/// Essentials <c>SecureStorage</c>/<c>Preferences</c> are reference-only stubs that
/// throw. Stores the 32-byte wrap-key on disk under the app data dir:
/// <list type="bullet">
/// <item><b>Windows</b>: sealed with DPAPI (CurrentUser).</item>
/// <item><b>Linux/macOS</b>: AES-GCM under a key derived from the user profile +
/// machine-id, file mode <c>0600</c>. Deters casual copying; not a hardware
/// keystore (see <see cref="IsHardwareBacked"/>). A libsecret/gnome-keyring backend
/// can later replace <see cref="ReadRaw"/>/<see cref="WriteRaw"/>.</item>
/// </list>
/// </summary>
public sealed class FileProtectionKeyProvider : IProtectionKeyProvider
{
    private const int KeyLen = 32;
    private readonly string _path;
    private readonly object _lock = new();
    private byte[]? _cached;

    /// <summary>True only when Windows DPAPI is actually available (package present).</summary>
    public bool IsHardwareBacked { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
        Type.GetType("System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData") != null;

    public FileProtectionKeyProvider()
        => _path = Path.Combine(AppDataPaths.AppDataDir(), "protection.key");

    public SecureBuffer? GetProtectionKey()
    {
        lock (_lock)
        {
            if (_cached != null) return Copy(_cached);
            byte[]? key = ReadRaw();
            if (key == null)
            {
                key = new byte[KeyLen];
                RandomNumberGenerator.Fill(key);
                WriteRaw(key);
            }
            _cached = key;
            return Copy(key);
        }
    }

    private byte[]? ReadRaw()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            byte[] blob = File.ReadAllBytes(_path);
            return IsHardwareBacked ? DpapiUnprotect(blob) : FileUnwrap(blob);
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
    }

    private void WriteRaw(byte[] key)
    {
        byte[] blob = IsHardwareBacked ? DpapiProtect(key) : FileWrap(key);
        File.WriteAllBytes(_path, blob);
        TryRestrictPermissions(_path);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiProtect(byte[] data)
        => DpapiInvoke("Protect", data);

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiUnprotect(byte[] blob)
        => DpapiInvoke("Unprotect", blob);

    // DPAPI (System.Security.Cryptography.ProtectedData) ships as a separate package
    // and only works on Windows. Rather than take that package reference just for the
    // Windows-only Avalonia case, call it reflectively; if it isn't present we throw
    // so ReadRaw/WriteRaw fall back to regenerating (WriteRaw uses FileWrap instead).
    private static byte[] DpapiInvoke(string method, byte[] input)
    {
        var t = Type.GetType("System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData");
        var scopeType = Type.GetType("System.Security.Cryptography.DataProtectionScope, System.Security.Cryptography.ProtectedData");
        if (t == null || scopeType == null)
            throw new PlatformNotSupportedException("DPAPI not available");
        object currentUser = Enum.Parse(scopeType, "CurrentUser");
        var mi = t.GetMethod(method, new[] { typeof(byte[]), typeof(byte[]), scopeType })!;
        return (byte[])mi.Invoke(null, new object[] { input, s_entropy, currentUser })!;
    }

    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("com.sovworks.edslite.net/protkey");

    // [12 nonce][16 tag][ciphertext]
    private static byte[] FileWrap(byte[] key)
    {
        byte[] kek = DeriveSeedKey();
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var cipher = new byte[key.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(kek, 16)) gcm.Encrypt(nonce, key, cipher, tag);
        Array.Clear(kek);
        var blob = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, blob, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, blob, nonce.Length + tag.Length, cipher.Length);
        return blob;
    }

    private static byte[] FileUnwrap(byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException("short blob");
        byte[] kek = DeriveSeedKey();
        try
        {
            var key = new byte[blob.Length - 28];
            using var gcm = new AesGcm(kek, 16);
            gcm.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), key);
            return key;
        }
        finally { Array.Clear(kek); }
    }

    private static byte[] DeriveSeedKey()
    {
        string seed = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "|" +
                      (SafeMachineId() ?? Environment.MachineName) + "|edslite-protkey-v1";
        return SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    private static string? SafeMachineId()
    {
        try
        {
            foreach (var p in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                if (File.Exists(p)) return File.ReadAllText(p).Trim();
        }
        catch { /* ignore */ }
        return null;
    }

    private static void TryRestrictPermissions(string path)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best effort */ }
    }

    private static SecureBuffer Copy(byte[] key)
    {
        var copy = (byte[])key.Clone();
        var buffer = new SecureBuffer(copy);
        Array.Clear(copy);
        return buffer;
    }
}
