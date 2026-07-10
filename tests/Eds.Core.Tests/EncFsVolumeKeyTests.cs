using System.Text;
using Eds.Core.Exceptions;
using Eds.Core.Fs.EncFs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase C: EncFS master-key derivation (password → PBKDF2 → unwrap volume key).
/// Round-trips the whole config→password→key stack; wrong passwords are rejected
/// by the checksum. Byte-for-byte interop with desktop EncFS still needs a real
/// volume (K1). See gap guide §4.3.
/// </summary>
public class EncFsVolumeKeyTests
{
    private static byte[] Seq(int n, int seed)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 17 + seed);
        return b;
    }

    private static Config NewConfig()
    {
        var cfg = new Config();
        cfg.InitNew("test");
        cfg.Salt = Seq(20, 5);
        cfg.KdfIterations = 1000; // low, for test speed
        return cfg;
    }

    [Fact]
    public void VolumeKey_Wrap_Then_Unwrap_RoundTrips()
    {
        var cfg = NewConfig();

        int vkLen;
        using (var fe = cfg.GetDataCodecInfo()!.GetFileEncDec()) vkLen = fe.KeySize;
        var volumeKey = Seq(vkLen, 99);

        var pwd = Encoding.UTF8.GetBytes("correct horse battery staple");
        var kek = EncFsVolumeKey.DeriveKey(pwd, cfg);
        cfg.EncryptedVolumeKey = EncFsVolumeKey.EncryptVolumeKey(kek, volumeKey, cfg);

        Assert.Equal(vkLen + EncFsVolumeKey.KeyChecksumBytes, cfg.EncryptedVolumeKey!.Length);

        var kek2 = EncFsVolumeKey.DeriveKey(pwd, cfg);
        var recovered = EncFsVolumeKey.DecryptVolumeKey(kek2, cfg);
        Assert.True(recovered.AsSpan().SequenceEqual(volumeKey));
    }

    [Fact]
    public void WrongPassword_IsRejected()
    {
        var cfg = NewConfig();
        int vkLen;
        using (var fe = cfg.GetDataCodecInfo()!.GetFileEncDec()) vkLen = fe.KeySize;
        var volumeKey = Seq(vkLen, 42);

        var kek = EncFsVolumeKey.DeriveKey(Encoding.UTF8.GetBytes("right-password"), cfg);
        cfg.EncryptedVolumeKey = EncFsVolumeKey.EncryptVolumeKey(kek, volumeKey, cfg);

        var wrongKek = EncFsVolumeKey.DeriveKey(Encoding.UTF8.GetBytes("wrong-password"), cfg);
        Assert.Throws<WrongPasswordException>(() => EncFsVolumeKey.DecryptVolumeKey(wrongKek, cfg));
    }

    [Fact]
    public void DeriveKey_IsDeterministic_AndLengthCorrect()
    {
        var salt = Seq(20, 3);
        var pwd = Encoding.UTF8.GetBytes("pw");
        var a = EncFsVolumeKey.DeriveKey(pwd, salt, 500, 24, 16);
        var b = EncFsVolumeKey.DeriveKey(pwd, salt, 500, 24, 16);
        Assert.Equal(40, a.Length); // keySize + ivSize
        Assert.True(a.AsSpan().SequenceEqual(b));

        var c = EncFsVolumeKey.DeriveKey(pwd, salt, 501, 24, 16); // different iterations
        Assert.False(a.AsSpan().SequenceEqual(c));
    }
}
