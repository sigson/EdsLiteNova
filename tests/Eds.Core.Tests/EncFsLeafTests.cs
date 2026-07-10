using System.Security.Cryptography;
using Eds.Core.Fs.EncFs;
using Eds.Core.Fs.EncFs.Macs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase C (EncFS) leaf-component tests: the B64 name codec and the HMAC-SHA1
/// MAC calculator. These are the self-contained, independently verifiable pieces
/// of EncFS. See porting gap guide §4.3.
/// </summary>
public class EncFsLeafTests
{
    // -- B64 name coding -------------------------------------------------

    private static string EncodeName(byte[] raw)
    {
        int len = raw.Length;
        int outLen = B64.B256ToB64Bytes(len);
        var buf = new byte[Math.Max(outLen, len)];
        Array.Copy(raw, buf, len);
        B64.ChangeBase2Inline(buf, 0, len, 8, 6, true);
        return B64.B64ToString(buf, 0, outLen);
    }

    private static byte[] DecodeName(string name)
    {
        var vals = B64.StringToB64(name);
        B64.ChangeBase2Inline(vals, 0, vals.Length, 6, 8, false);
        int outLen = B64.B64ToB256Bytes(vals.Length);
        var res = new byte[outLen];
        Array.Copy(vals, res, outLen);
        return res;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(31)]
    public void B64_Name_Encode_Decode_RoundTrips(int len)
    {
        var raw = new byte[len];
        for (int i = 0; i < len; i++) raw[i] = (byte)(i * 37 + 11);

        string encoded = EncodeName(raw);
        // encoded chars must be from the filesystem-safe alphabet (no '/', no '.')
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('.', encoded);

        var decoded = DecodeName(encoded);
        Assert.True(decoded.AsSpan().SequenceEqual(raw), $"round-trip failed for len={len}");
    }

    [Fact]
    public void B64_ByteCount_Helpers()
    {
        Assert.Equal(22, B64.B256ToB64Bytes(16));
        Assert.Equal(16, B64.B64ToB256Bytes(22));
        Assert.Equal(2, B64.B256ToB64Bytes(1));
        Assert.Equal(1, B64.B64ToB256Bytes(2));
    }

    [Fact]
    public void B64_String_Value_RoundTrip()
    {
        // Map every value 0..63 to a char and back.
        var vals = new byte[64];
        for (int i = 0; i < 64; i++) vals[i] = (byte)i;
        string s = B64.B64ToString(vals, 0, 64);
        var back = B64.StringToB64(s);
        Assert.True(back.AsSpan().SequenceEqual(vals));
    }

    // -- SHA1 MAC calculator --------------------------------------------

    private static byte[] ReferenceFold(byte[] keyPrefix, byte[] data)
    {
        using var h = new HMACSHA1(keyPrefix);
        var mac = h.ComputeHash(data); // 20 bytes
        var cut = new byte[8];
        for (int i = 0; i < mac.Length - 1; i++) cut[i % cut.Length] ^= mac[i];
        return cut;
    }

    [Fact]
    public void Sha1Mac_MatchesReferenceHmacFold()
    {
        const int keySize = 20;
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        var data = System.Text.Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");

        using var mac = new Sha1MacCalculator(keySize);
        mac.Init(key);
        var got = mac.CalcChecksum(data, 0, data.Length);

        var keyPrefix = new byte[keySize];
        Array.Copy(key, keyPrefix, keySize);
        var expected = ReferenceFold(keyPrefix, data);

        Assert.True(got.AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public void Sha1Mac_IsDeterministic_AndOffsetAware()
    {
        var key = new byte[24];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(255 - i);
        var data = new byte[100];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);

        using var mac = new Sha1MacCalculator(16);
        mac.Init(key);
        var a = mac.CalcChecksum(data, 10, 40);
        var b = mac.CalcChecksum(data, 10, 40);
        Assert.True(a.AsSpan().SequenceEqual(b)); // deterministic

        var c = mac.CalcChecksum(data, 11, 40);
        Assert.False(a.AsSpan().SequenceEqual(c)); // offset changes result
    }

    [Fact]
    public void Sha1Mac_ChainedIV_ChangesResult_AndUpdatesIV()
    {
        var key = new byte[20];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 3 + 5);
        var data = System.Text.Encoding.ASCII.GetBytes("chained-iv-test-payload");

        using var plain = new Sha1MacCalculator(20);
        plain.Init(key);
        var noChain = plain.CalcChecksum(data, 0, data.Length);

        using var chained = new Sha1MacCalculator(20);
        chained.Init(key);
        chained.SetChainedIV(new byte[8]); // enable with zero IV
        var first = chained.CalcChecksum(data, 0, data.Length);
        var ivAfterFirst = chained.GetChainedIV();

        // With a (nonzero) chained IV the result differs from the non-chained case,
        // and the chained IV advances to the produced checksum.
        Assert.False(noChain.AsSpan().SequenceEqual(first));
        Assert.NotNull(ivAfterFirst);
        Assert.True(first.AsSpan().SequenceEqual(ivAfterFirst!));

        // Next call chains again -> different output than the first.
        var second = chained.CalcChecksum(data, 0, data.Length);
        Assert.False(first.AsSpan().SequenceEqual(second));
    }
}
