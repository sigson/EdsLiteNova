using System.Text;
using Eds.Core.Containers;
using Eds.Core.Crypto;
using Eds.Core.Crypto.BlockCiphers;
using Eds.Core.Crypto.Engines;
using Eds.Core.Crypto.Hash;
using Eds.Core.Crypto.Kdf;
using Eds.Core.Fs;
using Xunit;

namespace Eds.Core.Tests;

public class CryptoKatTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Theory]
    [InlineData("000102030405060708090a0b0c0d0e0f", "00112233445566778899aabbccddeeff", "69c4e0d86a7b0430d8cdb78070b4c55a")]
    [InlineData("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", "00112233445566778899aabbccddeeff", "8ea2b7ca516745bfeafc49904b496089")]
    public void Aes_MatchesFips197(string keyHex, string ptHex, string ctHex)
    {
        using var aes = new Aes(keyHex.Length / 2);
        aes.Init(Hex(keyHex));
        var block = Hex(ptHex);
        aes.EncryptBlock(block);
        Assert.Equal(ctHex, Hex(block));
        aes.DecryptBlock(block);
        Assert.Equal(ptHex, Hex(block));
    }

    [Theory]
    [InlineData("", "9c1185a5c5e9fc54612808977ee8f548b2258d31")]
    [InlineData("abc", "8eb208f7e05d987a9b044a8e98c6b087f15a0bfc")]
    [InlineData("message digest", "5d0689ef49d2fae572b881b123a85ffa21595f36")]
    public void Ripemd160_MatchesReference(string msg, string expect)
    {
        using var md = new Ripemd160();
        Assert.Equal(expect, Hex(md.DoFinal(Encoding.ASCII.GetBytes(msg))));
    }

    [Theory]
    [InlineData("", "19fa61d75522a4669b44e39c1d2e1726c530232130d407f89afee0964997f7a73e83be698b288febcf88e3e03c4f0757ea8964e59b63d93708b138cc42a66eb3")]
    [InlineData("abc", "4e2448a4c6f486bb16b6562c73b4020bf3043e3a731bce721ae1b303d97e6d4c7181eebdb6c57e277d0e34957114cbd6c797fc9d95d8b582d225292076d4eef5")]
    public void Whirlpool_MatchesReference(string msg, string expect)
    {
        using var md = new Whirlpool();
        Assert.Equal(expect, Hex(md.DoFinal(Encoding.ASCII.GetBytes(msg))));
    }

    [Fact]
    public void Twofish256_MatchesOfficialVector()
    {
        using var tf = new Twofish(); // 32-byte key
        tf.Init(new byte[32]);
        var block = new byte[16];
        tf.EncryptBlock(block);
        Assert.Equal("57ff739d4dc92c1bd7fc01700cc8216f", Hex(block));
    }

    [Fact]
    public void Serpent256_MatchesOfficialVector()
    {
        using var sp = new Serpent(); // 32-byte key
        sp.Init(new byte[32]);
        var block = new byte[16];
        sp.EncryptBlock(block);
        Assert.Equal("49672ba898d98df95019180445491089", Hex(block));
    }

    [Fact]
    public void Serpent_Twofish_Gost_RoundTrip()
    {
        AssertRoundTrip(new Serpent(), 16);
        AssertRoundTrip(new Twofish(), 16);
        AssertRoundTrip(new Gost(), 8);
    }

    private static void AssertRoundTrip(IBlockCipher cipher, int blockSize)
    {
        using (cipher)
        {
            var key = new byte[cipher.KeySize];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + 1);
            cipher.Init(key);
            var block = new byte[blockSize];
            for (int i = 0; i < blockSize; i++) block[i] = (byte)(i * 3 + 5);
            var orig = (byte[])block.Clone();
            cipher.EncryptBlock(block);
            Assert.False(block.AsSpan().SequenceEqual(orig));
            cipher.DecryptBlock(block);
            Assert.True(block.AsSpan().SequenceEqual(orig));
        }
    }

    [Fact]
    public void AesXts_RoundTrip_MultiSector()
    {
        var key = new byte[64];
        for (int i = 0; i < 64; i++) key[i] = (byte)(i + 1);
        using var xts = new AesXts();
        xts.SetKey(key);
        xts.Init();

        var data = new byte[2048];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xff);
        var orig = (byte[])data.Clone();

        var iv = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv, 0);
        xts.SetIV(iv);
        xts.Encrypt(data, 0, data.Length);
        Assert.False(data.AsSpan().SequenceEqual(orig));
        // tweak active: repeating plaintext must not yield repeating ciphertext across sectors
        Assert.False(data.AsSpan(0, 512).SequenceEqual(data.AsSpan(512, 512)));

        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv, 0);
        xts.SetIV(iv);
        xts.Decrypt(data, 0, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(orig));
    }

    [Fact]
    public void AesCbc_MatchesNist80038a()
    {
        // CBC-AES256 (NIST SP 800-38A F.2.5/F.2.6). AesCbc uses a 256-bit key.
        var key = Hex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        var iv = Hex("000102030405060708090a0b0c0d0e0f");
        var pt = Hex("6bc1bee22e409f96e93d7e117393172a" +
                     "ae2d8a571e03ac9c9eb76fac45af8e51" +
                     "30c81c46a35ce411e5fbc1191a0a52ef" +
                     "f69f2445df4f9b17ad2b417be66c3710");
        const string expect = "f58c4c04d6e5f1ba779eabfb5f7bfbd6" +
                              "9cfc4e967edb808d679f777bc6702c7d" +
                              "39f23369a9d9bacfa530e26304231461" +
                              "b2eb05e2c39be9fcda6c19078c6a9d1b";
        using var cbc = new AesCbc();
        cbc.SetKey(key);
        cbc.Init();
        var buf = (byte[])pt.Clone();
        cbc.SetIV(iv);
        cbc.Encrypt(buf, 0, buf.Length);
        Assert.Equal(expect, Hex(buf));
        cbc.SetIV(iv);
        cbc.Decrypt(buf, 0, buf.Length);
        Assert.Equal(Hex(pt), Hex(buf));
    }

    [Theory]
    [InlineData("password", "salt", 1, 20, "0c60c80f961f0e71f3a9b524af6012062fe037a6")]
    [InlineData("password", "salt", 2, 20, "ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957")]
    [InlineData("password", "salt", 4096, 20, "4b007901b765489abead49d926f721d065a429c1")]
    public void Pbkdf2HmacSha1_MatchesRfc6070(string pass, string salt, int iters, int dkLen, string expect)
    {
        var kdf = new HashBasedPbkdf2(BclDigest.Sha1(), 64);
        var dk = kdf.DeriveKey(Encoding.ASCII.GetBytes(pass), Encoding.ASCII.GetBytes(salt), iters, dkLen);
        Assert.Equal(expect, Hex(dk));
    }
}

public class ContainerRoundTripTests
{
    [Fact]
    public void TrueCrypt_Create_Open_Read_Write()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"eds_test_{Guid.NewGuid():N}.tc");
        const long size = 512 * 1024;
        var password = "correct horse battery staple"u8.ToArray();
        var payload = Encoding.UTF8.GetBytes("round-trip payload 0123456789 ✔");

        try
        {
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, true))
            {
                var layout = new StdLayout();
                layout.SetPassword((byte[])password.Clone());
                layout.FormatNew(baseIo, size);
                using var vol = new EncryptedFile(baseIo, layout);
                vol.Seek(0);
                vol.Write(payload, 0, payload.Length);
                vol.Flush();
                layout.Dispose();
            }

            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, false))
            using (var container = new EdsContainer(baseIo))
            {
                Assert.True(container.Open(password));
                using var vol = container.GetEncryptedVolume();
                var read = new byte[payload.Length];
                vol.Seek(0);
                int n = vol.Read(read, 0, read.Length);
                Assert.Equal(payload.Length, n);
                Assert.True(read.AsSpan().SequenceEqual(payload));
            }

            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, false))
            using (var container = new EdsContainer(baseIo))
            {
                Assert.False(container.Open("wrong"u8.ToArray()));
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Luks1_Create_Open_Read_Write()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"eds_luks_{Guid.NewGuid():N}.luks");
        const long volumeSize = 256 * 1024;
        var password = "luks test password"u8.ToArray();
        var payload = Encoding.UTF8.GetBytes("LUKS round-trip payload ✔ 42");

        try
        {
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, true))
            {
                var layout = new LuksLayout();
                layout.SetPassword((byte[])password.Clone());
                layout.FormatNew(baseIo, volumeSize);
                using var vol = new EncryptedFile(baseIo, layout);
                vol.Seek(0);
                vol.Write(payload, 0, payload.Length);
                vol.Flush();
                layout.Dispose();
            }

            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, false))
            using (var container = new EdsContainer(baseIo))
            {
                Assert.True(container.Open(password));
                using var vol = container.GetEncryptedVolume();
                var read = new byte[payload.Length];
                vol.Seek(0);
                int n = vol.Read(read, 0, read.Length);
                Assert.Equal(payload.Length, n);
                Assert.True(read.AsSpan().SequenceEqual(payload));
            }

            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, false))
            using (var container = new EdsContainer(baseIo))
            {
                Assert.False(container.Open("wrong"u8.ToArray()));
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
