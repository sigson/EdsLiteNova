using System.Text;
using Eds.Core.Fs.EncFs;
using Eds.Core.Fs.EncFs.Codecs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase C: EncFS Config (encfs6.xml) parsing/writing and codec resolution.
/// See gap guide §4.3. Note the parser is validated against representative XML;
/// full desktop-EncFS interop still needs a real volume + password (K1).
/// </summary>
public class EncFsConfigTests
{
    private static string RepresentativeXml(byte[] keyData, byte[] salt) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE boost_serialization>
        <boost_serialization signature="serialization::archive" version="14">
          <cfg class_id="0" tracking_level="0" version="20">
            <version>20100713</version>
            <creator>EncFS 1.9.5</creator>
            <cipherAlg class_id="1" tracking_level="0" version="0">
              <name>ssl/aes</name>
              <major>3</major>
              <minor>0</minor>
            </cipherAlg>
            <nameAlg>
              <name>nameio/block</name>
              <major>4</major>
              <minor>0</minor>
            </nameAlg>
            <keySize>192</keySize>
            <blockSize>1024</blockSize>
            <uniqueIV>1</uniqueIV>
            <chainedNameIV>1</chainedNameIV>
            <externalIVChaining>0</externalIVChaining>
            <blockMACBytes>0</blockMACBytes>
            <blockMACRandBytes>0</blockMACRandBytes>
            <allowHoles>1</allowHoles>
            <encodedKeySize>{keyData.Length}</encodedKeySize>
            <encodedKeyData>{Convert.ToBase64String(keyData)}</encodedKeyData>
            <saltLen>{salt.Length}</saltLen>
            <saltData>{Convert.ToBase64String(salt)}</saltData>
            <kdfIterations>145203</kdfIterations>
            <desiredKDFDuration>500</desiredKDFDuration>
          </cfg>
        </boost_serialization>
        """;

    private static byte[] Seq(int n, int seed)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 13 + seed);
        return b;
    }

    [Fact]
    public void Parses_Representative_Config()
    {
        var keyData = Seq(44, 1);
        var salt = Seq(20, 7);
        var xml = RepresentativeXml(keyData, salt);

        var cfg = new Config();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        cfg.Read(ms);

        Assert.Equal(24, cfg.KeySize);      // 192 bits
        Assert.Equal(1024, cfg.BlockSize);
        Assert.True(cfg.UseUniqueIV);
        Assert.True(cfg.UseChainedNameIV);
        Assert.False(cfg.UseExternalFileIV);
        Assert.True(cfg.AllowHoles);
        Assert.Equal(145203, cfg.KdfIterations);
        Assert.NotNull(cfg.EncryptedVolumeKey);
        Assert.True(cfg.EncryptedVolumeKey!.AsSpan().SequenceEqual(keyData));
        Assert.True(cfg.Salt!.AsSpan().SequenceEqual(salt));

        Assert.Equal(AesDataCodecInfo.CodecName, cfg.GetDataCodecInfo()!.Name);
        Assert.Equal(BlockNameCodecInfo.CodecName, cfg.GetNameCodecInfo()!.Name);
        Assert.True(cfg.GetNameCodecInfo()!.UseChainedNamingIV());
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var cfg = new Config();
        cfg.InitNew("EDS test");
        cfg.EncryptedVolumeKey = Seq(44, 3);
        cfg.Salt = Seq(20, 9);
        cfg.KdfIterations = 100000;

        using var ms = new MemoryStream();
        cfg.Write(ms);
        ms.Position = 0;

        var cfg2 = new Config();
        cfg2.Read(ms);

        Assert.Equal(cfg.KeySize, cfg2.KeySize);
        Assert.Equal(cfg.BlockSize, cfg2.BlockSize);
        Assert.Equal(cfg.UseChainedNameIV, cfg2.UseChainedNameIV);
        Assert.Equal(cfg.KdfIterations, cfg2.KdfIterations);
        Assert.True(cfg2.EncryptedVolumeKey!.AsSpan().SequenceEqual(cfg.EncryptedVolumeKey!));
        Assert.True(cfg2.Salt!.AsSpan().SequenceEqual(cfg.Salt!));
        Assert.Equal(AesDataCodecInfo.CodecName, cfg2.GetDataCodecInfo()!.Name);
        Assert.Equal(BlockNameCodecInfo.CodecName, cfg2.GetNameCodecInfo()!.Name);
    }

    [Fact]
    public void Config_Builds_Working_NameCodec()
    {
        var cfg = new Config();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(RepresentativeXml(Seq(44, 1), Seq(20, 7))));
        cfg.Read(ms);

        // Build the name codec straight from the parsed config and round-trip a name.
        using var codec = cfg.GetNameCodecInfo()!.GetEncDec();
        var volumeKey = Seq(cfg.KeySize + 16, 42); // keySize + CBC IV size
        codec.Init(volumeKey);
        codec.SetIV(null);

        const string name = "Documents-2024";
        string enc = codec.EncodeName(name);
        Assert.NotEqual(name, enc);
        Assert.Equal(name, codec.DecodeName(enc));
    }

    [Fact]
    public void Unsupported_Algorithm_Throws()
    {
        string xml = RepresentativeXml(Seq(44, 1), Seq(20, 7))
            .Replace("ssl/aes", "ssl/blowfish");
        var cfg = new Config();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.ThrowsAny<Exception>(() => cfg.Read(ms));
    }
}
