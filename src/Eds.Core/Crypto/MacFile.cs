using System.Security.Cryptography;
using Eds.Core.Fs;
using Eds.Core.Fs.EncFs.Macs;
using Eds.Core.Fs.Util;

namespace Eds.Core.Crypto;

/// <summary>
/// Random-access file whose blocks carry an integrity MAC (and optional random
/// bytes) header. Faithful port of <c>crypto.MACFile</c> — a
/// <see cref="TransRandomAccessIO"/> where each real block is
/// <c>[macBytes MAC][randBytes random][data]</c>. Used by EncFS for per-block
/// integrity. The virtual size excludes the per-block overhead.
/// </summary>
public class MacFile : TransRandomAccessIO
{
    private byte[] _transBuffer;
    private readonly MacCalculator _macCalc;
    private readonly int _macBytes, _randBytes, _overhead;
    private readonly bool _forceDecode;
    private readonly RandomNumberGenerator? _random;

    public MacFile(IRandomAccessIO baseIo, MacCalculator macCalc, int blockSize,
        int macBytes, int randBytes, bool forceDecode)
        : base(baseIo, blockSize - macBytes - randBytes)
    {
        _macCalc = macCalc;
        _macBytes = macBytes;
        _randBytes = randBytes;
        _overhead = macBytes + randBytes;
        _forceDecode = forceDecode;
        _random = randBytes > 0 ? RandomNumberGenerator.Create() : null;
        _transBuffer = new byte[_bufferSize + _overhead];
        _length = CalcVirtPosition(baseIo.Length(), _bufferSize, _overhead);
    }

    public static long CalcVirtPosition(long realPos, int blockSize, int overhead)
    {
        int blockSizeWithOverhead = blockSize + overhead;
        long blockNum = (realPos + blockSizeWithOverhead - 1) / blockSizeWithOverhead;
        return realPos - blockNum * overhead;
    }

    public static int GetMacCheckedBuffer(byte[] baseBuffer, int offset, int count, long bufferPosition,
        byte[] dstBuffer, MacCalculator macCalc, int macBytes, int randBytes, bool allowSkip, bool forceDecode)
    {
        int resCount = count - macBytes - randBytes;
        Array.Copy(baseBuffer, offset + macBytes + randBytes, dstBuffer, offset, resCount);
        if (macBytes == 0 || (allowSkip && count > macBytes && EncryptedFile.IsBufferEmpty(baseBuffer, offset, count)))
            return resCount;
        byte fail = 0;
        byte[] mac = macCalc.CalcChecksum(baseBuffer, offset + macBytes, count - macBytes);
        for (int i = 0; i < macBytes; i++)
            fail |= (byte)(mac[i] ^ baseBuffer[macBytes - i - 1]);
        if (fail != 0)
        {
            string msg = "MAC comparison failure for the block at " + bufferPosition;
            if (!forceDecode) throw new IOException(msg);
        }
        return resCount;
    }

    public static void MakeMacCheckedBuffer(byte[] buf, int offset, int count, byte[] baseBuffer,
        MacCalculator macCalc, int macBytes, int randBytes, RandomNumberGenerator? random)
    {
        Array.Copy(buf, offset, baseBuffer, offset + macBytes + randBytes, count);
        if (randBytes > 0)
        {
            var rb = new byte[randBytes];
            random!.GetBytes(rb);
            Array.Copy(rb, 0, baseBuffer, offset + macBytes, randBytes);
        }
        if (macBytes > 0)
        {
            byte[] mac = macCalc.CalcChecksum(baseBuffer, offset + macBytes, count + randBytes);
            for (int i = 0; i < macBytes; i++)
                baseBuffer[offset + i] = mac[macBytes - i - 1];
        }
    }

    public override void Close(bool closeBase)
    {
        try { base.Close(closeBase); }
        finally
        {
            _macCalc.Dispose();
            Array.Clear(_transBuffer);
        }
    }

    protected override long CalcBasePosition(long position)
    {
        long blockNum = (position + _bufferSize - 1) / _bufferSize;
        return position + blockNum * _overhead;
    }

    protected override long CalcVirtPosition(long basePosition)
        => CalcVirtPosition(basePosition, _bufferSize, _overhead);

    protected override int ReadFromBaseAndTransformBuffer(byte[] buf, int offset, int count, long bufferPosition)
    {
        int bc = ReadFromBase(_transBuffer, offset, count + _overhead, bufferPosition);
        return bc > 0 ? TransformBufferFromBase(_transBuffer, offset, bc, bufferPosition, buf) : 0;
    }

    protected override int TransformBufferFromBase(byte[] baseBuffer, int offset, int count, long bufferPosition, byte[] dstBuffer)
        => GetMacCheckedBuffer(baseBuffer, offset, count, bufferPosition, dstBuffer,
            _macCalc, _macBytes, _randBytes, _allowSkip, _forceDecode);

    protected override void TransformBufferAndWriteToBase(byte[] buf, int offset, int count, long bufferPosition)
    {
        TransformBufferToBase(buf, offset, count, bufferPosition, _transBuffer);
        WriteToBase(_transBuffer, offset, count + _overhead, bufferPosition);
    }

    protected override void TransformBufferToBase(byte[] buf, int offset, int count, long bufferPosition, byte[] baseBuffer)
        => MakeMacCheckedBuffer(buf, offset, count, baseBuffer, _macCalc, _macBytes, _randBytes, _random);
}
