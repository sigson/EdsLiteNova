namespace Eds.Core.Fs.EncFs;

/// <summary>
/// Filename encoder/decoder for EncFS. Faithful port of <c>fs.encfs.NameCodec</c>.
/// Encodes a plaintext filename to a filesystem-safe encrypted string and back,
/// optionally chaining the parent directory IV (EncFS "chainedNameIV").
/// </summary>
public interface INameCodec : IDisposable
{
    string EncodeName(string plaintextName);
    string DecodeName(string encodedName);
    byte[]? GetChainedIV(string plaintextName);
    void Init(byte[] key);
    void SetIV(byte[]? iv);
    byte[]? GetIV();
    int IVSize { get; }
}
