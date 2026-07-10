using System.Buffers.Binary;
using System.Security.Cryptography;
using Eds.Core.Crypto;
using Eds.Core.Fs.EncFs.Ciphers;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.EncFs;

/// <summary>
/// A file inside an EncFS volume. Faithful port of <c>fs.encfs.File</c>. Layers,
/// from the raw file up: an optional 8-byte per-file IV header (stream-encrypted),
/// a <see cref="BufferedEncryptedFile"/> (per-block <c>blockIndex XOR fileIV</c>,
/// last block via the stream cipher), and an optional <see cref="MacFile"/> for
/// per-block integrity. The decrypted view is exposed as an
/// <see cref="IRandomAccessIO"/> / streams.
/// </summary>
public sealed class EncFsFile : FileWrapper
{
    private const int HeaderSize = 8;

    private readonly bool _enableIVHeader, _allowEmptyParts, _forceDecode;
    private readonly IDataCodecInfo _encInfo;
    private readonly byte[] _encryptionKey;
    private readonly byte[]? _externalIV;
    private readonly int _macBytes, _randBytes, _fileBlockSize;

    public EncFsFile(EncFsPath path, IFile realFile, IDataCodecInfo encInfo, byte[] encryptionKey,
        byte[]? externalIV, int fileBlockSize, bool enableHeader, bool allowEmptyParts,
        int macBytes, int randBytes, bool forceDecode)
        : base(path, realFile)
    {
        _encInfo = encInfo;
        _encryptionKey = encryptionKey;
        _externalIV = externalIV == null ? null : (byte[])externalIV.Clone();
        _fileBlockSize = fileBlockSize;
        _enableIVHeader = enableHeader;
        _allowEmptyParts = allowEmptyParts;
        _macBytes = macBytes;
        _randBytes = randBytes;
        _forceDecode = forceDecode;
    }

    public new EncFsPath Path => (EncFsPath)base.Path;
    public EncFsPath EncFsPathTyped => Path;

    public override string GetName() => Path.GetDecodedPath().GetFileName();

    public override IRandomAccessIO GetRandomAccessIO(FileAccessMode accessMode)
    {
        IRandomAccessIO baseIo = GetBase().GetRandomAccessIO(accessMode);
        try
        {
            switch (accessMode)
            {
                case FileAccessMode.Read:
                    if (_enableIVHeader && GetBase().GetSize() < HeaderSize) return baseIo;
                    return InitEncryptedFile(baseIo, InitFileLayoutFromBase(baseIo, forWrite: false));
                case FileAccessMode.ReadWrite:
                    if (Path.Exists() && GetBase().GetSize() >= HeaderSize)
                        return InitEncryptedFile(baseIo, InitFileLayoutFromBase(baseIo, forWrite: false));
                    goto case FileAccessMode.Write;
                case FileAccessMode.ReadWriteTruncate:
                case FileAccessMode.Write:
                    return InitEncryptedFile(baseIo, InitFileLayoutFromBase(baseIo, forWrite: true));
                case FileAccessMode.WriteAppend:
                    if (_enableIVHeader) throw new ArgumentException("Can't write header in WriteAppend mode");
                    return InitEncryptedFile(baseIo, InitFileLayout(null));
                default:
                    throw new ArgumentException("Wrong access mode");
            }
        }
        catch (Exception e)
        {
            baseIo.Dispose();
            throw new IOException("Failed opening encrypted file", e);
        }
    }

    public override Stream GetInputStream()
        => new RandomAccessInputStream(GetRandomAccessIO(FileAccessMode.Read), ownsIo: true);

    public override Stream GetOutputStream()
        => new RandomAccessOutputStream(GetRandomAccessIO(FileAccessMode.ReadWriteTruncate), ownsIo: true);

    public override long GetSize()
    {
        long size = GetBase().GetSize();
        if (_enableIVHeader && size >= HeaderSize) size -= HeaderSize;
        if (_randBytes > 0 || _macBytes > 0)
            size = MacFile.CalcVirtPosition(size, _fileBlockSize - _randBytes - _macBytes, _randBytes + _macBytes);
        return size;
    }

    public override void Rename(string newName)
    {
        if (_externalIV != null || Path.NamingCodecInfo.UseChainedNamingIV())
        {
            IFile newFile = ((EncFsPath)Path.GetParentPath()!).GetDirectory().CreateFile(newName);
            using (var outp = newFile.GetOutputStream()) CopyToOutputStream(outp, 0, 0, null);
            Delete();
            SetPath(newFile.Path);
        }
        else
        {
            StringPathUtil newEncoded = ((EncFsPath)Path.GetParentPath()!).CalcCombinedEncodedParts(newName);
            base.Rename(newEncoded.GetFileName());
        }
    }

    public override void MoveTo(IDirectory newParent)
    {
        if (_externalIV != null || Path.NamingCodecInfo.UseChainedNamingIV())
        {
            IFile newFile = newParent.CreateFile(GetName());
            using (var outp = newFile.GetOutputStream()) CopyToOutputStream(outp, 0, 0, null);
            Delete();
            SetPath(newFile.Path);
        }
        else
            base.MoveTo(newParent);
    }

    protected override IPath GetPathFromBasePath(IPath basePath) => Path.EncFs.GetPathFromRealPath(basePath)!;

    // ---- layering ------------------------------------------------------

    private IRandomAccessIO InitEncryptedFile(IRandomAccessIO baseIo, IEncryptedFileLayout fl)
    {
        var ee = new BufferedEncryptedFile(baseIo, fl, 1);
        ee.SetAllowSkip(_allowEmptyParts);
        if (_macBytes > 0 || _randBytes > 0)
        {
            var mac = _encInfo.GetChecksumCalculator();
            mac.Init(_encryptionKey);
            var mf = new MacFile(ee, mac, _fileBlockSize, _macBytes, _randBytes, _forceDecode);
            mf.SetAllowSkip(_allowEmptyParts);
            return mf;
        }
        return ee;
    }

    private IEncryptedFileLayout InitFileLayoutFromBase(IRandomAccessIO baseIo, bool forWrite)
    {
        Header? h = null;
        if (_enableIVHeader)
        {
            if (forWrite)
            {
                h = Header.NewRandom();
                WriteHeader(baseIo, h);
            }
            else
                h = ReadHeader(baseIo);
        }
        return InitFileLayout(h);
    }

    private IEncryptedFileLayout InitFileLayout(Header? h)
    {
        IFileEncryptionEngine ee = new BlockAndStreamCipher(_encInfo.GetFileEncDec(), _encInfo.GetStreamEncDec());
        ee.SetKey(_encryptionKey);
        ee.Init();
        return h == null ? new FileLayout(ee, 0, null) : new FileLayout(ee, HeaderSize, h.IV);
    }

    private Header ReadHeader(IRandomAccessIO baseIo)
    {
        var buf = new byte[HeaderSize];
        baseIo.Seek(0);
        if (ReadFull(baseIo, buf) != HeaderSize) throw new IOException("Failed reading header");
        var ee = _encInfo.GetStreamEncDec();
        try
        {
            ee.SetKey(_encryptionKey);
            ee.Init();
            ee.SetIV(_externalIV ?? new byte[ee.IVSize]);
            ee.Decrypt(buf, 0, buf.Length);
        }
        finally { ee.Dispose(); }
        return new Header { IV = buf };
    }

    private void WriteHeader(IRandomAccessIO baseIo, Header header)
    {
        var data = (byte[])header.IV.Clone();
        var ee = _encInfo.GetStreamEncDec();
        try
        {
            ee.SetKey(_encryptionKey);
            ee.Init();
            ee.SetIV(_externalIV ?? new byte[ee.IVSize]);
            ee.Encrypt(data, 0, data.Length);
        }
        finally { ee.Dispose(); }
        baseIo.Seek(0);
        baseIo.Write(data, 0, data.Length);
    }

    private static int ReadFull(IRandomAccessIO io, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = io.Read(buf, total, buf.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private sealed class Header
    {
        public byte[] IV = new byte[HeaderSize];
        public static Header NewRandom()
        {
            var h = new Header();
            RandomNumberGenerator.Fill(h.IV);
            return h;
        }
    }

    private sealed class FileLayout : IEncryptedFileLayout, IDisposable
    {
        private readonly int _encryptedDataOffset;
        private readonly IFileEncryptionEngine _dataEncDec;
        private readonly byte[]? _fileIV;

        public FileLayout(IFileEncryptionEngine engine, int dataOffset, byte[]? fileIV)
        {
            _dataEncDec = engine;
            _encryptedDataOffset = dataOffset;
            _fileIV = fileIV;
        }

        public long EncryptedDataOffset => _encryptedDataOffset;
        public IFileEncryptionEngine Engine => _dataEncDec;

        public void SetEncryptionEngineIV(IFileEncryptionEngine eng, long decryptedVolumeOffset)
        {
            var iv = new byte[eng.IVSize];
            BinaryPrimitives.WriteInt64BigEndian(iv, decryptedVolumeOffset / _dataEncDec.FileBlockSize);
            if (_fileIV != null)
                for (int i = 0; i < _fileIV.Length; i++) iv[i] ^= _fileIV[i];
            eng.SetIV(iv);
        }

        public void Dispose() => _dataEncDec.Dispose();
    }
}
