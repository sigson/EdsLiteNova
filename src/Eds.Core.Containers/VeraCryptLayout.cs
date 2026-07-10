using Eds.Core.Crypto.Hash;

namespace Eds.Core.Containers;

/// <summary>
/// VeraCrypt volume layout. Faithful port of <c>veracrypt.VolumeLayout</c>:
/// "VERA" signature, higher min program version, SHA-256 added, and the large
/// iteration counts (655331 for RIPEMD-160, else 500000; or 15000+PIM*1000).
/// </summary>
public sealed class VeraCryptLayout : StdLayout
{
    private static readonly byte[] Sig = "VERA"u8.ToArray();
    private const short CompatibleProgramVersion = 0x010b;

    private int _numIterations;

    public static int GetKDFIterationsFromPim(int pim) => 15000 + pim * 1000;

    public override void SetNumKDFIterations(int num) => _numIterations = num;

    public override void Dispose()
    {
        base.Dispose();
        _numIterations = 0;
    }

    public override IReadOnlyList<IMessageDigest> GetSupportedHashFuncs()
    {
        var l = new List<IMessageDigest>(base.GetSupportedHashFuncs())
        {
            BclDigest.Sha256(),
        };
        return l;
    }

    protected override byte[] GetHeaderSignature() => Sig;
    protected override short GetMinCompatibleProgramVersion() => CompatibleProgramVersion;

    protected override int GetMKKDFNumIterations(IMessageDigest hashFunc)
    {
        if (_numIterations > 0)
            return GetKDFIterationsFromPim(_numIterations);
        return string.Equals(hashFunc.Algorithm, "ripemd160", StringComparison.OrdinalIgnoreCase)
            ? 655331
            : 500000;
    }
}
