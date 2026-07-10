using System.Security.Cryptography;
using Eds.Core.Crypto.Native;

namespace Eds.Core.Crypto.Hash;

/// <summary>
/// Minimal streaming digest abstraction mirroring the subset of
/// <c>java.security.MessageDigest</c> used by HMAC/PBKDF and the container
/// layouts. Kept small on purpose so both BCL-backed (SHA) and native-backed
/// (RIPEMD-160, Whirlpool) implementations can satisfy it.
/// </summary>
public interface IMessageDigest : IDisposable
{
    /// <summary>Lower-case-comparable algorithm name, e.g. "sha-512", "ripemd160".</summary>
    string Algorithm { get; }
    int DigestLength { get; }

    void Reset();
    void Update(byte value);
    void Update(byte[] data);
    void Update(byte[] data, int offset, int len);

    /// <summary>Finalises into <paramref name="output"/> at <paramref name="offset"/> and resets.</summary>
    void DoFinal(byte[] output, int offset);

    /// <summary>One-shot digest of <paramref name="input"/>; leaves the instance reset.</summary>
    byte[] DoFinal(byte[] input);
}

/// <summary>SHA-1/256/512 backed by the BCL <see cref="IncrementalHash"/>.</summary>
public sealed class BclDigest : IMessageDigest
{
    private readonly HashAlgorithmName _name;
    private IncrementalHash _hash;

    public BclDigest(HashAlgorithmName name, string algorithm, int digestLength)
    {
        _name = name;
        Algorithm = algorithm;
        DigestLength = digestLength;
        _hash = IncrementalHash.CreateHash(name);
    }

    public static BclDigest Sha1() => new(HashAlgorithmName.SHA1, "sha-1", 20);
    public static BclDigest Sha256() => new(HashAlgorithmName.SHA256, "sha-256", 32);
    public static BclDigest Sha512() => new(HashAlgorithmName.SHA512, "sha-512", 64);

    public string Algorithm { get; }
    public int DigestLength { get; }

    public void Reset()
    {
        _hash.Dispose();
        _hash = IncrementalHash.CreateHash(_name);
    }

    public void Update(byte value) => _hash.AppendData(stackalloc byte[] { value });
    public void Update(byte[] data) => _hash.AppendData(data);
    public void Update(byte[] data, int offset, int len) => _hash.AppendData(data.AsSpan(offset, len));

    public void DoFinal(byte[] output, int offset)
    {
        Span<byte> tmp = stackalloc byte[DigestLength];
        _hash.GetHashAndReset(tmp);
        tmp.CopyTo(output.AsSpan(offset));
    }

    public byte[] DoFinal(byte[] input)
    {
        _hash.AppendData(input);
        var res = new byte[DigestLength];
        _hash.GetHashAndReset(res);
        return res;
    }

    public void Dispose() => _hash.Dispose();
}

/// <summary>Base for native-backed digests (RIPEMD-160, Whirlpool).</summary>
public abstract class NativeDigestBase : IMessageDigest
{
    static NativeDigestBase() => NativeLibraryResolver.EnsureRegistered();

    protected nint Ctx;

    protected NativeDigestBase(string algorithm, int digestLength)
    {
        Algorithm = algorithm;
        DigestLength = digestLength;
        Ctx = InitNative();
    }

    public string Algorithm { get; }
    public int DigestLength { get; }

    public void Reset() => ResetNative(Ctx);
    public void Update(byte value) => UpdateNative(Ctx, new[] { value }, 0, 1);
    public void Update(byte[] data) => UpdateNative(Ctx, data, 0, data.Length);
    public void Update(byte[] data, int offset, int len) => UpdateNative(Ctx, data, offset, len);

    public void DoFinal(byte[] output, int offset)
    {
        Span<byte> tmp = stackalloc byte[DigestLength];
        FinalNative(Ctx, tmp);
        tmp.CopyTo(output.AsSpan(offset));
        // native final resets internal state via reset() semantics; be explicit:
        ResetNative(Ctx);
    }

    public byte[] DoFinal(byte[] input)
    {
        UpdateNative(Ctx, input, 0, input.Length);
        var res = new byte[DigestLength];
        FinalNative(Ctx, res);
        ResetNative(Ctx);
        return res;
    }

    protected abstract nint InitNative();
    protected abstract void ResetNative(nint ctx);
    protected abstract void UpdateNative(nint ctx, byte[] data, int offset, int len);
    protected abstract void FinalNative(nint ctx, Span<byte> output);
    protected abstract void FreeNative(nint ctx);

    public void Dispose()
    {
        if (Ctx != nint.Zero)
        {
            FreeNative(Ctx);
            Ctx = nint.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~NativeDigestBase() => Dispose();
}

/// <summary>RIPEMD-160 (20-byte digest) via the native shim.</summary>
public sealed class Ripemd160 : NativeDigestBase
{
    public Ripemd160() : base("ripemd160", 20) { }
    protected override nint InitNative() => NativeCrypto.Ripemd160Init();
    protected override void ResetNative(nint ctx) => NativeCrypto.Ripemd160Reset(ctx);
    protected override void UpdateNative(nint ctx, byte[] data, int offset, int len) => NativeCrypto.Ripemd160Update(ctx, data, offset, len);
    protected override void FinalNative(nint ctx, Span<byte> output) => NativeCrypto.Ripemd160Final(ctx, output);
    protected override void FreeNative(nint ctx) => NativeCrypto.Ripemd160Free(ctx);
}

/// <summary>Whirlpool (64-byte digest) via the native shim.</summary>
public sealed class Whirlpool : NativeDigestBase
{
    public Whirlpool() : base("whirlpool", 64) { }
    protected override nint InitNative() => NativeCrypto.WhirlpoolInit();
    protected override void ResetNative(nint ctx) => NativeCrypto.WhirlpoolReset(ctx);
    protected override void UpdateNative(nint ctx, byte[] data, int offset, int len) => NativeCrypto.WhirlpoolUpdate(ctx, data, offset, len);
    protected override void FinalNative(nint ctx, Span<byte> output) => NativeCrypto.WhirlpoolFinal(ctx, output);
    protected override void FreeNative(nint ctx) => NativeCrypto.WhirlpoolFree(ctx);
}
