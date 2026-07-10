using System.Buffers.Binary;
using Eds.Core.Crypto.Hash;
using Eds.Core.Exceptions;

namespace Eds.Core.Crypto.Kdf;

/// <summary>
/// PBKDF2 over HMAC. Faithful port of <c>crypto.kdf.PBKDF</c>. The original's
/// <c>ProgressReporter</c> (percent + cancellation) is replaced by the idiomatic
/// <see cref="IProgress{T}"/> + <see cref="CancellationToken"/> pair.
///
/// This is the most CPU-intensive operation when opening a container (VeraCrypt
/// uses hundreds of thousands of iterations); always run it off the UI thread.
/// </summary>
public abstract class Pbkdf
{
    private const int CounterLength = 4;
    private int _finishedIterations;
    private int _totalIterations;

    public IProgress<int>? Progress { get; set; }
    public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

    /// <summary>Container-usable KDFs: SHA-512, RIPEMD-160, Whirlpool.</summary>
    public static IEnumerable<Pbkdf> GetAvailablePbkdfs() => new Pbkdf[]
    {
        new HmacSha512Kdf(),
        new HmacRipemd160Kdf(),
        new HmacWhirlpoolKdf(),
    };

    public byte[] DeriveKey(byte[] srcKey, byte[] salt, int keyLen)
        => DeriveKey(srcKey, salt, DefaultIterationsCount, keyLen);

    public byte[] DeriveKey(byte[] srcKey, byte[] salt, int iterations, int keyLen)
    {
        using var hmac = InitHmac(srcKey);
        int digestLength = hmac.DigestLength;
        byte[] u = new byte[digestLength];
        byte[] res = new byte[keyLen];
        int l = keyLen % digestLength != 0 ? 1 + keyLen / digestLength : keyLen / digestLength;
        int r = keyLen - (l - 1) * digestLength;

        _finishedIterations = 0;
        _totalIterations = iterations * l;

        int b;
        for (b = 1; b < l; b++)
        {
            DeriveBlock(hmac, salt, iterations, u, b);
            Array.Copy(u, 0, res, (b - 1) * digestLength, digestLength);
        }
        DeriveBlock(hmac, salt, iterations, u, b);
        Array.Copy(u, 0, res, (b - 1) * digestLength, r);
        Array.Clear(u);
        return res;
    }

    private void DeriveBlock(Hmac hmac, byte[] salt, int iterations, byte[] u, int block)
    {
        int digestLength = hmac.DigestLength;

        byte[] init = new byte[salt.Length + CounterLength];
        Array.Copy(salt, 0, init, 0, salt.Length);
        BinaryPrimitives.WriteInt32BigEndian(init.AsSpan(salt.Length), block);

        byte[] j = new byte[digestLength];
        hmac.CalcHmac(init, 0, init.Length, j);
        Array.Copy(j, 0, u, 0, digestLength);

        int prevPrc = -1;
        byte[] k = new byte[digestLength];
        for (int c = 1; c < iterations; c++)
        {
            hmac.CalcHmac(j, 0, j.Length, k);
            for (int i = 0; i < digestLength; i++)
            {
                u[i] ^= k[i];
                j[i] = k[i];
            }
            if (Progress != null)
            {
                int prc = (int)(((float)_finishedIterations++ * 100) / _totalIterations);
                if (prc != prevPrc)
                {
                    prevPrc = prc;
                    Progress.Report(prc);
                }
            }
            if (CancellationToken.IsCancellationRequested)
                throw new OperationCancelledException();
        }
        Array.Clear(j);
        Array.Clear(k);
    }

    protected abstract Hmac InitHmac(byte[] srcKey);
    protected virtual int DefaultIterationsCount => 1000;
}

/// <summary>PBKDF2 with an arbitrary <see cref="IMessageDigest"/>. Mirrors HashBasedPBKDF2.</summary>
public class HashBasedPbkdf2 : Pbkdf
{
    private readonly IMessageDigest _md;
    private readonly int _blockSize;

    public HashBasedPbkdf2(IMessageDigest md) : this(md, GuessBlockSize(md)) { }

    public HashBasedPbkdf2(IMessageDigest md, int blockSize)
    {
        _md = md;
        _blockSize = blockSize;
    }

    protected override Hmac InitHmac(byte[] password)
    {
        _md.Reset();
        return new Hmac(password, _md, _blockSize);
    }

    private static int GuessBlockSize(IMessageDigest md)
    {
        var n = md.Algorithm.ToLowerInvariant();
        return n is "sha-512" or "sha512" ? 128 : 64;
    }
}

/// <summary>PBKDF2/HMAC-SHA-512 (block 128).</summary>
public sealed class HmacSha512Kdf : Pbkdf
{
    protected override Hmac InitHmac(byte[] srcKey) => new(srcKey, BclDigest.Sha512(), 128);
}

/// <summary>PBKDF2/HMAC-SHA-1 (block 64).</summary>
public sealed class HmacSha1Kdf : Pbkdf
{
    protected override Hmac InitHmac(byte[] srcKey) => new(srcKey, BclDigest.Sha1(), 64);
}

/// <summary>PBKDF2/HMAC-RIPEMD-160 (block 64).</summary>
public sealed class HmacRipemd160Kdf : Pbkdf
{
    protected override Hmac InitHmac(byte[] srcKey) => new(srcKey, new Ripemd160(), 64);
}

/// <summary>PBKDF2/HMAC-Whirlpool (block 64).</summary>
public sealed class HmacWhirlpoolKdf : Pbkdf
{
    protected override Hmac InitHmac(byte[] srcKey) => new(srcKey, new Whirlpool(), 64);
}
