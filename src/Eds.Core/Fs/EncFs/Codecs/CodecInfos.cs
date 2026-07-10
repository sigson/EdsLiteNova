using Eds.Core.Crypto;
using Eds.Core.Fs.EncFs.Ciphers;
using Eds.Core.Fs.EncFs.Macs;

namespace Eds.Core.Fs.EncFs.Codecs;

/// <summary>AES data codec ("ssl/aes"). Port of <c>codecs.data.AESDataCodecInfo</c>.</summary>
public sealed class AesDataCodecInfo : IDataCodecInfo
{
    public const string CodecName = "ssl/aes";

    private Config? _config;

    public IFileEncryptionEngine GetFileEncDec() => new AesCbcFileCipher(_config!.KeySize, _config.BlockSize);
    public IEncryptionEngine GetStreamEncDec() => new AesCfbStreamCipher(_config!.KeySize);
    public MacCalculator GetChecksumCalculator() => new Sha1MacCalculator(_config!.KeySize);

    public IAlgInfo Select(Config config) => new AesDataCodecInfo { _config = config };

    public string Name => CodecName;
    public string Descr => "AES: 16 byte block cipher";
    public int Version1 => 3;
    public int Version2 => 0;
}

/// <summary>Base for filename codec infos. Port of <c>codecs.name.NameCodecInfoBase</c>.</summary>
public abstract class NameCodecInfoBase : INameCodecInfo
{
    protected Config? Cfg;

    public bool UseChainedNamingIV() => Cfg!.UseChainedNameIV;

    public IAlgInfo Select(Config config)
    {
        var info = CreateNew();
        info.Cfg = config;
        return info;
    }

    public abstract INameCodec GetEncDec();
    public abstract string Name { get; }
    public abstract string Descr { get; }
    public abstract int Version1 { get; }
    public abstract int Version2 { get; }

    protected abstract NameCodecInfoBase CreateNew();
}

/// <summary>Block filename codec ("nameio/block", base64). Port of <c>BlockNameCodecInfo</c>.</summary>
public sealed class BlockNameCodecInfo : NameCodecInfoBase
{
    public const string CodecName = "nameio/block";

    public override INameCodec GetEncDec()
    {
        var dci = Cfg!.GetDataCodecInfo()!;
        return new BlockNameCipher(dci.GetFileEncDec(), dci.GetChecksumCalculator(), false);
    }

    public override string Name => CodecName;
    public override string Descr => "Block: Block encoding, hides file name size somewhat";
    public override int Version1 => 4;
    public override int Version2 => 0;
    protected override NameCodecInfoBase CreateNew() => new BlockNameCodecInfo();
}

/// <summary>Case-sensitive block filename codec ("nameio/block32", base32). Port of <c>BlockCSNameCodecInfo</c>.</summary>
public sealed class BlockCsNameCodecInfo : NameCodecInfoBase
{
    public const string CodecName = "nameio/block32";

    public override INameCodec GetEncDec()
    {
        var dci = Cfg!.GetDataCodecInfo()!;
        return new BlockNameCipher(dci.GetFileEncDec(), dci.GetChecksumCalculator(), true);
    }

    public override string Name => CodecName;
    public override string Descr => "Block32: Block encoding with base32 output for case-sensitive systems";
    public override int Version1 => 4;
    public override int Version2 => 0;
    protected override NameCodecInfoBase CreateNew() => new BlockCsNameCodecInfo();
}

/// <summary>Stream filename codec ("nameio/stream"). Port of <c>StreamNameCodecInfo</c>.</summary>
public sealed class StreamNameCodecInfo : NameCodecInfoBase
{
    public const string CodecName = "nameio/stream";

    public override INameCodec GetEncDec()
    {
        var dci = Cfg!.GetDataCodecInfo()!;
        return new StreamNameCipher(dci.GetStreamEncDec(), dci.GetChecksumCalculator());
    }

    public override string Name => CodecName;
    public override string Descr => "Stream: Stream encoding, keeps filenames as short as possible";
    public override int Version1 => 2;
    public override int Version2 => 1;
    protected override NameCodecInfoBase CreateNew() => new StreamNameCodecInfo();
}

/// <summary>Null filename codec ("nameio/null"). Port of <c>NullNameCodecInfo</c>.</summary>
public sealed class NullNameCodecInfo : NameCodecInfoBase
{
    public const string CodecName = "nameio/null";

    public override INameCodec GetEncDec() => new NullNameCipher();

    public override string Name => CodecName;
    public override string Descr => "Null: No encryption of filenames";
    public override int Version1 => 1;
    public override int Version2 => 0;
    protected override NameCodecInfoBase CreateNew() => new NullNameCodecInfo();
}

/// <summary>
/// Registry of supported EncFS codecs. Mirrors <c>FS.getSupportedDataCodecs()</c> /
/// <c>getSupportedNameCodecs()</c>. These are unbound templates matched by name +
/// version during config parsing.
/// </summary>
public static class EncFsCodecs
{
    public static IReadOnlyList<IDataCodecInfo> SupportedDataCodecs { get; } =
        new IDataCodecInfo[] { new AesDataCodecInfo() };

    public static IReadOnlyList<INameCodecInfo> SupportedNameCodecs { get; } =
        new INameCodecInfo[]
        {
            new BlockNameCodecInfo(),
            new StreamNameCodecInfo(),
            new NullNameCodecInfo(),
            new BlockCsNameCodecInfo(),
        };
}
