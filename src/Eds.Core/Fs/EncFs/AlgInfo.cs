using Eds.Core.Crypto;
using Eds.Core.Fs.EncFs.Macs;

namespace Eds.Core.Fs.EncFs;

/// <summary>
/// Algorithm descriptor bound to a <see cref="Config"/>. Faithful port of
/// <c>fs.encfs.AlgInfo</c>. Registry entries are unbound "templates";
/// <see cref="Select"/> returns a copy bound to a specific config.
/// </summary>
public interface IAlgInfo
{
    IAlgInfo Select(Config config);
    string Name { get; }
    string Descr { get; }
    int Version1 { get; }
    int Version2 { get; }
}

/// <summary>Data (block/stream) codec descriptor. Port of <c>DataCodecInfo</c>.</summary>
public interface IDataCodecInfo : IAlgInfo
{
    IFileEncryptionEngine GetFileEncDec();
    IEncryptionEngine GetStreamEncDec();
    MacCalculator GetChecksumCalculator();
}

/// <summary>Filename codec descriptor. Port of <c>NameCodecInfo</c>.</summary>
public interface INameCodecInfo : IAlgInfo
{
    INameCodec GetEncDec();
    bool UseChainedNamingIV();
}
