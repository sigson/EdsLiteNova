using System.Buffers.Binary;
using Eds.Core.Crypto;
using Eds.Core.Crypto.Kdf;
using Eds.Core.Exceptions;

namespace Eds.Core.Fs.EncFs;

/// <summary>
/// Derives and (un)wraps the EncFS master (volume) key. Faithful port of the key
/// logic in <c>fs.encfs.FS</c>. The password is stretched with PBKDF2-HMAC-SHA1
/// (salt + iterations from the config) to a key-encryption key of
/// <c>keySize + ivSize</c> bytes; that KEK stream-decrypts the stored
/// <see cref="Config.EncryptedVolumeKey"/> (whose first 4 bytes are a MAC-32
/// checksum used as the IV). A checksum mismatch means the wrong password.
///
/// This completes the EncFS crypto stack (config → password → master key). It is
/// kept standalone (not embedded in the not-yet-ported FS class) so it can be
/// unit-tested on its own.
/// </summary>
public static class EncFsVolumeKey
{
    public const int KeyChecksumBytes = 4;

    public static byte[] DeriveKey(byte[] password, byte[] salt, int iterations, int keySize, int ivSize)
    {
        var kdf = new HmacSha1Kdf();
        return kdf.DeriveKey(password, salt, iterations, keySize + ivSize);
    }

    public static byte[] DeriveKey(byte[] password, Config config)
    {
        int ivSize;
        using (var fe = config.GetDataCodecInfo()!.GetFileEncDec()) ivSize = fe.IVSize;
        return DeriveKey(password, config.Salt!, config.KdfIterations, config.KeySize, ivSize);
    }

    public static byte[] DecryptVolumeKey(byte[] derivedKey, Config config)
    {
        var dci = config.GetDataCodecInfo()!;
        var encrypted = config.EncryptedVolumeKey
                        ?? throw new InvalidOperationException("No encrypted volume key in config");

        int checksum = 0;
        for (int i = 0; i < KeyChecksumBytes; i++) checksum = (checksum << 8) | (encrypted[i] & 0xFF);

        var volumeKey = new byte[encrypted.Length - KeyChecksumBytes];
        Array.Copy(encrypted, KeyChecksumBytes, volumeKey, 0, volumeKey.Length);

        using (var ee = dci.GetStreamEncDec())
        {
            ee.SetKey(derivedKey);
            ee.Init();
            ee.SetIV(MakeIv(ee.IVSize, checksum));
            ee.Decrypt(volumeKey, 0, volumeKey.Length);
        }

        using (var cc = dci.GetChecksumCalculator())
        {
            cc.Init(derivedKey);
            int checksum2 = cc.Calc32(volumeKey, 0, volumeKey.Length);
            if (checksum2 != checksum)
                throw new WrongPasswordException();
        }
        return volumeKey;
    }

    public static byte[] EncryptVolumeKey(byte[] derivedKey, byte[] volumeKey, Config config)
    {
        var dci = config.GetDataCodecInfo()!;

        int checksum;
        using (var cc = dci.GetChecksumCalculator())
        {
            cc.Init(derivedKey);
            checksum = cc.Calc32(volumeKey, 0, volumeKey.Length);
        }

        var res = new byte[volumeKey.Length + KeyChecksumBytes];
        Array.Copy(volumeKey, 0, res, KeyChecksumBytes, volumeKey.Length);

        using (var ee = dci.GetStreamEncDec())
        {
            ee.SetKey(derivedKey);
            ee.Init();
            ee.SetIV(MakeIv(ee.IVSize, checksum));
            ee.Encrypt(res, KeyChecksumBytes, volumeKey.Length);
        }

        int c = checksum;
        for (int i = 1; i <= KeyChecksumBytes; i++) { res[KeyChecksumBytes - i] = (byte)c; c >>= 8; }
        return res;
    }

    /// <summary>Builds the stream-cipher IV buffer: the 32-bit checksum as a big-endian long.</summary>
    private static byte[] MakeIv(int ivSize, int checksum)
    {
        var iv = new byte[ivSize];
        BinaryPrimitives.WriteInt64BigEndian(iv, checksum & 0xFFFFFFFFL);
        return iv;
    }
}
