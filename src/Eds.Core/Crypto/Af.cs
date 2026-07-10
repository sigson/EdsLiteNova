using System.Buffers.Binary;
using System.Security.Cryptography;
using Eds.Core.Crypto.Hash;

namespace Eds.Core.Crypto;

/// <summary>
/// LUKS anti-forensic (AF) splitter/merger. Faithful port of <c>crypto.AF</c>.
/// The master key is diffused across many "stripes" so that wiping any part
/// destroys the key. <see cref="Merge"/> reconstructs the key on open;
/// <see cref="Split"/> produces the stored material on create.
/// </summary>
public sealed class Af
{
    public const int SectorSize = 512;

    private readonly IMessageDigest _hash;
    private readonly int _blockSize;

    public Af(IMessageDigest hash, int blockSize)
    {
        _hash = hash;
        _blockSize = blockSize;
    }

    public static int CalcNumRequiredSectors(int blockSize, int numBlocks)
    {
        int afSize = blockSize * numBlocks;
        return (afSize + (SectorSize - 1)) / SectorSize;
    }

    public int CalcNumRequiredSectors(int numBlocks) => CalcNumRequiredSectors(_blockSize, numBlocks);

    public void Split(byte[] src, int srcOffset, byte[] dest, int destOffset, int blockNumber)
    {
        var block = new byte[_blockSize];
        var tmp = new byte[_blockSize];
        for (int i = 0; i < blockNumber - 1; i++)
        {
            RandomNumberGenerator.Fill(tmp);
            Array.Copy(tmp, 0, dest, destOffset + _blockSize * i, _blockSize);
            XorBlock(dest, destOffset + i * _blockSize, block, 0, block);
            Diffuse(block, 0, block, 0, _blockSize);
        }
        XorBlock(src, srcOffset, dest, destOffset + _blockSize * (blockNumber - 1), block);
    }

    public void Merge(byte[] src, int srcOffset, byte[] dest, int destOffset, int blockNumber)
    {
        var block = new byte[_blockSize];
        for (int i = 0; i < blockNumber - 1; i++)
        {
            XorBlock(src, srcOffset + i * _blockSize, block, 0, block);
            Diffuse(block, 0, block, 0, _blockSize);
        }
        XorBlock(src, srcOffset + _blockSize * (blockNumber - 1), dest, destOffset, block);
    }

    private static void XorBlock(byte[] src, int srcOffset, byte[] dst, int dstOffset, byte[] xorBlock)
    {
        for (int i = 0; i < xorBlock.Length; i++)
            dst[dstOffset + i] = (byte)(src[srcOffset + i] ^ xorBlock[i]);
    }

    private void Diffuse(byte[] src, int srcOffset, byte[] dst, int dstOffset, int len)
    {
        int ds = _hash.DigestLength;
        int blocks = len / ds;
        int padding = len % ds;
        for (int i = 0; i < blocks; i++)
            HashBuf(src, srcOffset + ds * i, dst, dstOffset + ds * i, ds, i);
        if (padding > 0)
            HashBuf(src, srcOffset + ds * blocks, dst, dstOffset + ds * blocks, padding, blocks);
    }

    private void HashBuf(byte[] src, int srcOffset, byte[] dst, int dstOffset, int len, int iv)
    {
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, iv);
        _hash.Reset();
        _hash.Update(prefix.ToArray());
        _hash.Update(src, srcOffset, len);
        var res = new byte[_hash.DigestLength];
        _hash.DoFinal(res, 0);
        Array.Copy(res, 0, dst, dstOffset, Math.Min(res.Length, dst.Length - dstOffset));
    }
}
