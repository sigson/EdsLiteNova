using Eds.Core.Fs;
using Eds.Core.Fs.Util;

namespace Eds.Core.Crypto;

/// <summary>
/// Buffered transparent encrypting file. Faithful port of the original
/// <c>crypto.EncryptedFile</c> (a <see cref="TransRandomAccessIO"/> subclass).
/// Unlike the container-oriented <see cref="EncryptedFile"/> (unbuffered,
/// whole-range), this processes one buffer at a time and decrypts/encrypts each
/// buffer with the IV the layout supplies for that buffer position. With
/// <c>bufferSizeInBlocks == 1</c> every file block gets its own IV and the final
/// short block is passed to the engine at its real length — which is exactly what
/// EncFS needs (per-block <c>blockIndex XOR fileIV</c>, last block via the stream
/// cipher of <c>BlockAndStreamCipher</c>). It also works for containers (XTS/CBC
/// advance the tweak internally across a multi-block buffer).
/// </summary>
public class BufferedEncryptedFile : TransRandomAccessIO
{
    public const int DefaultBufferSizeInBlocks = 16;

    private readonly long _dataOffset;
    private readonly int _fileBlockSize;
    private readonly IEncryptedFileLayout _layout;
    private byte[] _transBuffer;

    public BufferedEncryptedFile(IRandomAccessIO baseIo, IEncryptedFileLayout layout)
        : this(baseIo, layout, DefaultBufferSizeInBlocks) { }

    public BufferedEncryptedFile(IRandomAccessIO baseIo, IEncryptedFileLayout layout, int bufferSizeInBlocks)
        : base(baseIo, bufferSizeInBlocks * layout.Engine.FileBlockSize)
    {
        _layout = layout;
        _dataOffset = layout.EncryptedDataOffset;
        _fileBlockSize = layout.Engine.FileBlockSize;
        _transBuffer = new byte[_bufferSize];
        _length = baseIo.Length() - _dataOffset;
    }

    public override void Close(bool closeBase)
    {
        try { base.Close(closeBase); }
        finally
        {
            (_layout as IDisposable)?.Dispose();
            Array.Clear(_transBuffer);
        }
    }

    protected override long CalcBasePosition(long position) => position + _dataOffset;
    protected override long CalcVirtPosition(long basePosition) => basePosition - _dataOffset;

    protected override int TransformBufferFromBase(byte[] baseBuffer, int offset, int count, long bufferPosition, byte[] dstBuffer)
    {
        if (!ReferenceEquals(baseBuffer, dstBuffer))
            Array.Copy(baseBuffer, offset, dstBuffer, offset, count);

        if (!_allowSkip)
            DecryptBuffer(dstBuffer, offset, count, bufferPosition);
        else
        {
            for (int i = 0; i < count;)
            {
                int curSize = Math.Min(count - i, _fileBlockSize);
                if (curSize != _fileBlockSize || !EncryptedFile.IsBufferEmpty(dstBuffer, offset + i, curSize))
                    DecryptBuffer(dstBuffer, offset + i, curSize, bufferPosition + i);
                i += curSize;
            }
        }
        return count;
    }

    protected override void TransformBufferAndWriteToBase(byte[] buf, int offset, int count, long bufferPosition)
    {
        TransformBufferToBase(buf, offset, count, bufferPosition, _transBuffer);
        WriteToBase(_transBuffer, offset, count, bufferPosition);
    }

    protected override void TransformBufferToBase(byte[] buf, int offset, int count, long bufferPosition, byte[] baseBuffer)
    {
        Array.Copy(buf, offset, baseBuffer, offset, count);
        if (!_allowSkip)
            EncryptBuffer(baseBuffer, offset, count, bufferPosition);
        else
        {
            for (int i = 0; i < count;)
            {
                int curSize = Math.Min(count - i, _fileBlockSize);
                if (curSize != _fileBlockSize || !EncryptedFile.IsBufferEmpty(baseBuffer, offset + i, curSize))
                    EncryptBuffer(baseBuffer, offset + i, curSize, bufferPosition + i);
                i += curSize;
            }
        }
    }

    protected void DecryptBuffer(byte[] buf, int offset, int count, long bufferPosition)
    {
        var ee = _layout.Engine;
        _layout.SetEncryptionEngineIV(ee, bufferPosition);
        ee.Decrypt(buf, offset, count);
    }

    protected void EncryptBuffer(byte[] buf, int offset, int count, long bufferPosition)
    {
        var ee = _layout.Engine;
        _layout.SetEncryptionEngineIV(ee, bufferPosition);
        ee.Encrypt(buf, offset, count);
    }
}
