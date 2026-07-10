using System.Security.Cryptography;
using System.Text;

namespace Eds.Core.Crypto;

/// <summary>
/// Small utility for protecting sensitive settings values and deriving stable
/// location ids. Managed re-imagining of <c>crypto.SimpleCrypto</c> (§2.5 of the
/// gap guide).
///
/// <para>Two roles:
/// <list type="bullet">
/// <item><see cref="CalcStringMd5"/> — stable id from a location URI string
/// (used verbatim by the original to key a container/EncFS location);</item>
/// <item><see cref="Encrypt"/>/<see cref="Decrypt"/> — protect the optionally
/// saved container password (and custom KDF iteration count) written into the
/// per-location settings blob.</item>
/// </list></para>
///
/// <para><b>Compatibility note.</b> The gap guide is explicit that byte-for-byte
/// compatibility with data written by the Android <c>SimpleCrypto</c> is <em>not</em>
/// required (old Android settings are not being migrated). This implementation is
/// therefore a clean, self-contained AES-CBC-256 scheme over the BCL — a random
/// 16-byte IV prepended to the ciphertext, key derived from the protection key
/// with PBKDF2-HMAC-SHA256. The protection key itself should come from platform
/// secret storage (Keystore/Keychain/DPAPI). The MD5 helper is kept identical in
/// spirit (hash of the UTF-8 bytes, lowercase hex) because it only ever feeds an
/// opaque, internal identifier.</para>
/// </summary>
public static class SimpleCrypto
{
    /// <summary>Lowercase hex MD5 of the UTF-8 bytes of <paramref name="s"/>. Mirrors <c>calcStringMD5</c>.</summary>
    public static string CalcStringMd5(string s)
        => ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>Lowercase hex encoding. Mirrors <c>toHexString</c>.</summary>
    public static string ToHexString(byte[] data) => Convert.ToHexStringLower(data);

    /// <summary>Parses lowercase/uppercase hex. Mirrors <c>fromHexString</c>.</summary>
    public static byte[] FromHexString(string hex) => Convert.FromHexString(hex);

    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int KeyBits = 256;
    private const int Pbkdf2Iterations = 20000;

    /// <summary>
    /// Encrypts <paramref name="data"/> with a key derived from
    /// <paramref name="protectionKey"/> and returns a self-describing base64 blob
    /// (<c>salt || iv || ciphertext</c>). When no protection key is available the
    /// caller should store the data unprotected (hex) instead.
    /// </summary>
    public static string Encrypt(ReadOnlySpan<byte> protectionKey, byte[] data)
    {
        Span<byte> salt = stackalloc byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        byte[] key = DeriveKey(protectionKey, salt);
        try
        {
            using var aes = Aes.Create();
            aes.KeySize = KeyBits;
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();
            byte[] cipher = aes.EncryptCbc(data, aes.IV);
            byte[] outBuf = new byte[SaltSize + IvSize + cipher.Length];
            salt.CopyTo(outBuf);
            aes.IV.CopyTo(outBuf, SaltSize);
            cipher.CopyTo(outBuf, SaltSize + IvSize);
            return Convert.ToBase64String(outBuf);
        }
        finally { Array.Clear(key); }
    }

    /// <summary>Reverses <see cref="Encrypt"/>. Throws on tampering / wrong key.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> protectionKey, string blob)
    {
        byte[] all = Convert.FromBase64String(blob);
        if (all.Length < SaltSize + IvSize)
            throw new CryptographicException("Protected value is too short.");
        byte[] salt = all[..SaltSize];
        byte[] iv = all[SaltSize..(SaltSize + IvSize)];
        byte[] cipher = all[(SaltSize + IvSize)..];
        byte[] key = DeriveKey(protectionKey, salt);
        try
        {
            using var aes = Aes.Create();
            aes.KeySize = KeyBits;
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            return aes.DecryptCbc(cipher, iv);
        }
        finally { Array.Clear(key); }
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> protectionKey, ReadOnlySpan<byte> salt)
        => Rfc2898DeriveBytes.Pbkdf2(protectionKey, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBits / 8);
}
