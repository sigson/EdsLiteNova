using Eds.Core.Crypto;
using Eds.Core.Fs.EncFs.Ciphers;
using Eds.Core.Fs.EncFs.Macs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase C: EncFS cipher-layer round-trip tests (CipherBase HMAC-IV derivation,
/// AES-CBC file cipher, AES-CFB stream cipher, BlockAndStream composite, and the
/// name ciphers). Round-trips prove internal invertibility and that the B64+MAC+
/// cipher pipeline is self-consistent. Byte-for-byte parity with desktop EncFS
/// still requires real reference vectors (cross-task K1). See gap guide §4.3.
/// </summary>
public class EncFsCipherTests
{
    private const int KeySize = 32; // AES-256
    private const int IvPart = 16;  // CBC/CFB IV size that CipherBase appends to the key

    private static byte[] MakeKey()
    {
        var k = new byte[KeySize + IvPart];
        for (int i = 0; i < k.Length; i++) k[i] = (byte)(i * 7 + 3);
        return k;
    }

    private static byte[] MakeFileIv()
    {
        var iv = new byte[8];
        for (int i = 0; i < 8; i++) iv[i] = (byte)(0x10 + i);
        return iv;
    }

    private static byte[] MakeData(int n)
    {
        var d = new byte[n];
        for (int i = 0; i < n; i++) d[i] = (byte)(i * 131 + 29);
        return d;
    }

    [Fact]
    public void AesCbcFileCipher_RoundTrips()
    {
        var key = MakeKey();
        var fileIv = MakeFileIv();

        using var c = new AesCbcFileCipher(KeySize, 1024);
        c.SetKey(key);
        c.Init();

        var data = MakeData(1024);
        var orig = (byte[])data.Clone();

        c.SetIV(fileIv);
        c.Encrypt(data, 0, data.Length);
        Assert.False(data.AsSpan().SequenceEqual(orig));

        c.SetIV(fileIv);
        c.Decrypt(data, 0, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(orig));
    }

    [Fact]
    public void AesCfbStreamCipher_RoundTrips_ArbitraryLength()
    {
        var key = MakeKey();
        var fileIv = MakeFileIv();

        using var c = new AesCfbStreamCipher(KeySize);
        c.SetKey(key);
        c.Init();

        var data = MakeData(37); // not a block multiple - stream cipher handles it
        var orig = (byte[])data.Clone();

        c.SetIV(fileIv);
        c.Encrypt(data, 0, data.Length);
        Assert.False(data.AsSpan().SequenceEqual(orig));

        c.SetIV(fileIv);
        c.Decrypt(data, 0, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(orig));
    }

    [Fact]
    public void BlockAndStreamCipher_DispatchesAndRoundTrips()
    {
        var key = MakeKey();
        var fileIv = MakeFileIv();

        using var c = new BlockAndStreamCipher(
            new AesCbcFileCipher(KeySize, 1024),
            new AesCfbStreamCipher(KeySize));
        c.SetKey(key);
        c.Init();

        // full block -> block cipher path
        var full = MakeData(1024);
        var fullOrig = (byte[])full.Clone();
        c.SetIV(fileIv);
        c.Encrypt(full, 0, full.Length);
        c.SetIV(fileIv);
        c.Decrypt(full, 0, full.Length);
        Assert.True(full.AsSpan().SequenceEqual(fullOrig));

        // partial block -> stream cipher path
        var part = MakeData(100);
        var partOrig = (byte[])part.Clone();
        c.SetIV(fileIv);
        c.Encrypt(part, 0, part.Length);
        c.SetIV(fileIv);
        c.Decrypt(part, 0, part.Length);
        Assert.True(part.AsSpan().SequenceEqual(partOrig));
    }

    [Fact]
    public void NullNameCipher_IsIdentity()
    {
        using var codec = new NullNameCipher();
        Assert.Equal("hello.txt", codec.EncodeName("hello.txt"));
        Assert.Equal("hello.txt", codec.DecodeName("hello.txt"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("file.txt")]
    [InlineData("a-longer-directory-name-here")]
    [InlineData("Ünîcödë-имя")]
    public void BlockNameCipher_Base64_RoundTrips(string name)
    {
        var key = MakeKey();
        using var codec = new BlockNameCipher(
            new AesCbcFileCipher(KeySize, 1024), new Sha1MacCalculator(KeySize), caseSensitive: false);
        codec.Init(key);
        codec.SetIV(null); // no chained directory IV

        string enc = codec.EncodeName(name);
        Assert.DoesNotContain('/', enc);
        Assert.DoesNotContain('.', enc);
        Assert.NotEqual(name, enc);
        Assert.Equal(name, codec.DecodeName(enc));
    }

    [Fact]
    public void BlockNameCipher_Base32_CaseSensitive_RoundTrips()
    {
        var key = MakeKey();
        using var codec = new BlockNameCipher(
            new AesCbcFileCipher(KeySize, 1024), new Sha1MacCalculator(KeySize), caseSensitive: true);
        codec.Init(key);
        codec.SetIV(null);

        const string name = "MixedCase_Name_123";
        string enc = codec.EncodeName(name);
        Assert.Equal(name, codec.DecodeName(enc));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("file.txt")]
    [InlineData("another-name-2024")]
    public void StreamNameCipher_RoundTrips(string name)
    {
        var key = MakeKey();
        using var codec = new StreamNameCipher(
            new AesCfbStreamCipher(KeySize), new Sha1MacCalculator(KeySize));
        codec.Init(key);
        codec.SetIV(null);

        string enc = codec.EncodeName(name);
        Assert.DoesNotContain('/', enc);
        Assert.NotEqual(name, enc);
        Assert.Equal(name, codec.DecodeName(enc));
    }

    [Fact]
    public void NameCipher_ChainedIV_AffectsEncoding()
    {
        var key = MakeKey();

        using var noChain = new BlockNameCipher(
            new AesCbcFileCipher(KeySize, 1024), new Sha1MacCalculator(KeySize), false);
        noChain.Init(key);
        noChain.SetIV(null);
        string a = noChain.EncodeName("document");

        using var chained = new BlockNameCipher(
            new AesCbcFileCipher(KeySize, 1024), new Sha1MacCalculator(KeySize), false);
        chained.Init(key);
        chained.SetIV(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        string b = chained.EncodeName("document");

        // A chained directory IV changes the encrypted name, and it must still
        // round-trip under that same IV.
        Assert.NotEqual(a, b);
        Assert.Equal("document", chained.DecodeName(b));
    }
}
