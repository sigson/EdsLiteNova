using System.Buffers.Binary;
using System.IO.Hashing;
using Eds.Core.Crypto;
using Eds.Core.Crypto.Hash;
using Eds.Core.Exceptions;
using Eds.Core.Fs;

namespace Eds.Core.Containers;

/// <summary>
/// TrueCrypt standard volume layout. Faithful port of
/// <c>truecrypt.StdLayout</c>, including the "algorithm brute-force": when the
/// hash/cipher are unknown, every supported hash x engine combination is tried
/// (each a full PBKDF2 + header decrypt + signature/CRC check).
/// </summary>
public class StdLayout : VolumeLayoutBase
{
    public const int HeaderSize = 64 * 1024;

    protected const int ReservedHeaderSize = 4 * HeaderSize;
    protected const short MinAllowedHeaderVersion = 3;
    protected const short CurrentHeaderVersion = 5;
    protected const short HeaderCrcOffset = 252;
    protected const short DataKeyAreaMaxSize = 256;
    protected const short DataAreaKeyOffset = 256;
    protected const int SaltSize = 64;
    protected const int VolumeSizeOffset = 116;

    /// <summary>Minimum compatible TrueCrypt program version (see EdsContainer).</summary>
    public const short CompatibleTcVersion = 0x71a;

    protected static readonly byte[] TcSig = "TRUE"u8.ToArray();

    protected long EncryptedAreaStart;
    protected long VolumeSize;
    protected long InputSize;

    public StdLayout() => EncryptedAreaStart = 2 * HeaderSize;

    public override long EncryptedDataOffset => EncryptedAreaStart;
    public override long GetEncryptedDataSize(long fileSize) => VolumeSize;

    public override IReadOnlyList<IFileEncryptionEngine> GetSupportedEncryptionEngines()
        => EncryptionEnginesRegistry.GetSupportedEncryptionEngines();

    public override IReadOnlyList<IMessageDigest> GetSupportedHashFuncs() => new IMessageDigest[]
    {
        BclDigest.Sha512(),
        new Ripemd160(),
        new Whirlpool(),
    };

    public void SetContainerSize(long containerSize)
    {
        InputSize = containerSize;
        VolumeSize = CalcVolumeSize(containerSize);
    }

    public override bool ReadHeader(IRandomAccessIO input)
    {
        CheckReadHeaderPrereqs();
        InputSize = input.Length();
        input.Seek(GetHeaderOffset());
        int hs = GetEffectiveHeaderSize();
        var encryptedHeader = new byte[hs + GetEncryptedHeaderPartOffset()];
        if (ReadBytes(input, encryptedHeader, hs) != hs)
            return false;
        if (IsUnsupportedHeaderType(encryptedHeader))
            return false;
        byte[] salt = GetSaltFromHeader(encryptedHeader);
        if (SelectAlgosAndDecodeHeader(encryptedHeader, salt))
        {
            PrepareEncryptionEngineForPayload();
            return true;
        }
        return false;
    }

    protected void PrepareEncryptionEngineForPayload()
    {
        Engine.SetKey(MasterKey);
        Engine.Init();
    }

    protected virtual bool IsUnsupportedHeaderType(byte[] encryptedHeader) => false;

    protected byte[] GetSaltFromHeader(byte[] headerData)
    {
        var salt = new byte[SaltSize];
        Array.Copy(headerData, 0, salt, 0, SaltSize);
        return salt;
    }

    protected bool IsValidSign(byte[] headerData)
    {
        var sig = GetHeaderSignature();
        int offset = GetEncryptedHeaderPartOffset();
        for (int i = 0; i < sig.Length; i++)
            if (headerData[offset + i] != sig[i]) return false;
        return true;
    }

    protected bool SelectAlgosAndDecodeHeader(byte[] encryptedHeaderData, byte[] salt)
    {
        if (HashFunc == null)
        {
            foreach (var md in GetSupportedHashFuncs())
            {
                var ee = TryHashFunc(encryptedHeaderData, salt, md);
                if (ee != null)
                {
                    SetEngine(ee);
                    HashFunc = md;
                    return true;
                }
            }
        }
        else
        {
            var ee = TryHashFunc(encryptedHeaderData, salt, HashFunc);
            if (ee != null)
            {
                SetEngine(ee);
                return true;
            }
        }
        return false;
    }

    protected IFileEncryptionEngine? TryHashFunc(byte[] encryptedHeaderData, byte[] salt, IMessageDigest hashFunc)
    {
        byte[]? prevKey = null;
        try
        {
            if (EncEngine != null)
            {
                if (TryEncryptionEngine(encryptedHeaderData, salt, hashFunc, EncEngine, ref prevKey))
                    return EncEngine;
            }
            else
            {
                foreach (var ee in GetSupportedEncryptionEngines())
                {
                    if (TryEncryptionEngine(encryptedHeaderData, salt, hashFunc, ee, ref prevKey))
                        return ee;
                }
            }
        }
        finally
        {
            if (prevKey != null) Array.Clear(prevKey);
        }
        return null;
    }

    protected bool TryEncryptionEngine(byte[] encryptedHeaderData, byte[] salt, IMessageDigest hashFunc,
        IFileEncryptionEngine ee, ref byte[]? prevKey)
    {
        OpeningProgressReporter?.SetCurrentKdfName(hashFunc.Algorithm);
        OpeningProgressReporter?.SetCurrentEncryptionAlgName(ee.CipherName);

        byte[]? key = prevKey;
        if (key == null || key.Length < ee.KeySize)
        {
            key = DeriveHeaderKey(ee, hashFunc, salt);
            prevKey = key;
        }
        if (DecryptAndDecodeHeader(encryptedHeaderData, ee, key))
            return true;
        ee.Dispose();
        return false;
    }

    protected bool DecryptAndDecodeHeader(byte[] encryptedHeader, IFileEncryptionEngine ee, byte[] key)
    {
        byte[]? decryptedHeader = null;
        try
        {
            decryptedHeader = DecryptHeader(encryptedHeader, ee, key);
            if (decryptedHeader == null) return false;

            if (MasterKey != null) Array.Clear(MasterKey);
            MasterKey = new byte[ee.KeySize];
            DecodeHeader(decryptedHeader);
            return true;
        }
        finally
        {
            if (decryptedHeader != null) Array.Clear(decryptedHeader);
        }
    }

    protected virtual int GetEncryptedHeaderPartOffset() => SaltSize;

    protected virtual int GetMKKDFNumIterations(IMessageDigest hashFunc)
        => string.Equals(hashFunc.Algorithm, "ripemd160", StringComparison.OrdinalIgnoreCase) ? 2000 : 1000;

    protected byte[]? DecryptHeader(byte[] encryptedData, IFileEncryptionEngine ee, byte[] key)
    {
        ee.SetIV(new byte[ee.IVSize]);
        ee.SetKey(key);
        ee.Init();
        var header = (byte[])encryptedData.Clone();
        int ofs = GetEncryptedHeaderPartOffset();
        try
        {
            ee.Decrypt(header, ofs, header.Length - ofs);
        }
        catch (EncryptionEngineException)
        {
            return null;
        }
        return IsValidSign(header) ? header : null;
    }

    protected byte[] DeriveHeaderKey(IFileEncryptionEngine ee, IMessageDigest md, byte[] salt)
    {
        int keySize = ee.KeySize;
        if (EncEngine == null)
        {
            foreach (var eng in GetSupportedEncryptionEngines())
                if (eng.KeySize > keySize) keySize = eng.KeySize;
        }
        return DeriveKey(keySize, md, Password!, salt, GetMKKDFNumIterations(md));
    }

    protected virtual byte[] GetHeaderSignature() => TcSig;

    protected virtual short GetMinCompatibleProgramVersion() => CompatibleTcVersion;

    protected long LoadVolumeSize(ReadOnlySpan<byte> headerData)
    {
        long vs = BinaryPrimitives.ReadInt64BigEndian(headerData.Slice(VolumeSizeOffset, 8));
        return vs == 0 ? InputSize - EncryptedAreaStart : vs;
    }

    protected void DecodeHeader(byte[] data)
    {
        int encPartOffset = GetEncryptedHeaderPartOffset();
        var span = data.AsSpan();

        int p = encPartOffset + GetHeaderSignature().Length; // offset 68 for TC
        short headerVersion = BinaryPrimitives.ReadInt16BigEndian(span.Slice(p, 2));
        if (headerVersion < MinAllowedHeaderVersion || headerVersion > CurrentHeaderVersion)
            throw new WrongContainerVersionException();

        var crc = new Crc32();
        crc.Append(data.AsSpan(encPartOffset, HeaderCrcOffset - encPartOffset));
        int computedHeaderCrc = (int)crc.GetCurrentHashAsUInt32();
        int storedHeaderCrc = BinaryPrimitives.ReadInt32BigEndian(span.Slice(HeaderCrcOffset, 4));
        if (computedHeaderCrc != storedHeaderCrc)
            throw new HeaderCrcException();

        // offset 70: min program version
        int programVer = BinaryPrimitives.ReadInt16BigEndian(span.Slice(p + 2, 2));
        if (programVer > CompatibleTcVersion)
            throw new WrongContainerVersionException();

        // offset 72: volume key area CRC32
        int volumeKeyAreaCrc32 = BinaryPrimitives.ReadInt32BigEndian(span.Slice(p + 4, 4));

        // offset 108: encrypted area start
        EncryptedAreaStart = BinaryPrimitives.ReadInt64BigEndian(span.Slice(108, 8));

        VolumeSize = LoadVolumeSize(span);

        crc.Reset();
        crc.Append(data.AsSpan(DataAreaKeyOffset, DataKeyAreaMaxSize));
        if ((int)crc.GetCurrentHashAsUInt32() != volumeKeyAreaCrc32)
            throw new HeaderCrcException();

        Array.Copy(data, DataAreaKeyOffset, MasterKey!, 0, MasterKey!.Length);
    }

    protected virtual long GetHeaderOffset() => 0;
    protected virtual int GetEffectiveHeaderSize() => 512;

    // ---- write path (create new containers; enables full round-trip tests) ---

    public override void InitNew()
    {
        HashFunc ??= BclDigest.Sha512();
        if (EncEngine == null) SetEngine(new Crypto.Engines.AesXts());
        base.InitNew();
    }

    protected long GetBackupHeaderOffset() => InputSize - 2 * HeaderSize;

    protected void CheckWriteHeaderPrereqs()
    {
        if (EncEngine == null || HashFunc == null || Password == null || MasterKey == null)
            throw new InvalidOperationException("Header data is not initialized");
    }

    public void WriteHeader(IRandomAccessIO output)
    {
        CheckWriteHeaderPrereqs();
        byte[] headerData = EncodeHeader();
        EncryptAndWriteHeaderData(output, headerData);
        PrepareEncryptionEngineForPayload();
    }

    protected virtual long CalcHiddenVolumeSize(long volumeSize) => 0;

    protected byte[] EncodeHeader()
    {
        int encPartOffset = GetEncryptedHeaderPartOffset();
        int total = GetEffectiveHeaderSize() + encPartOffset;
        var buf = new byte[total];
        var span = buf.AsSpan();

        var salt = new byte[SaltSize];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        salt.CopyTo(span);

        int p = encPartOffset;
        GetHeaderSignature().CopyTo(span.Slice(p)); p += GetHeaderSignature().Length;
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(p, 2), CurrentHeaderVersion); p += 2;
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(p, 2), GetMinCompatibleProgramVersion()); p += 2;

        var mk = new byte[DataKeyAreaMaxSize];
        Array.Copy(MasterKey!, mk, MasterKey!.Length);
        var keyCrc = new Crc32();
        keyCrc.Append(mk);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(p, 4), (int)keyCrc.GetCurrentHashAsUInt32());
        p += 4;

        p += 16; // skip creation times
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(p, 8), CalcHiddenVolumeSize(VolumeSize)); p += 8;
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(p, 8), VolumeSize); p += 8;
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(p, 8), EncryptedAreaStart); p += 8;
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(p, 8), VolumeSize); p += 8;
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(p, 4), 0); p += 4;          // flags
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(p, 4), SectorSize); p += 4; // sector size

        var headerCrc = new Crc32();
        headerCrc.Append(buf.AsSpan(encPartOffset, HeaderCrcOffset - encPartOffset));
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(HeaderCrcOffset, 4), (int)headerCrc.GetCurrentHashAsUInt32());

        mk.CopyTo(span.Slice(DataAreaKeyOffset));
        Array.Clear(mk);
        return buf;
    }

    protected void EncryptAndWriteHeaderData(IRandomAccessIO output, byte[] headerData)
    {
        byte[] salt = GetSaltFromHeader(headerData);
        byte[] key = DeriveHeaderKey(EncEngine!, HashFunc!, salt);
        EncryptHeader(headerData, key);
        Array.Clear(key);
        WriteHeaderData(output, headerData, GetHeaderOffset());
        WriteHeaderData(output, headerData, GetBackupHeaderOffset());
    }

    protected void WriteHeaderData(IRandomAccessIO output, byte[] encryptedHeaderData, long offset)
    {
        output.Seek(offset);
        output.Write(encryptedHeaderData, 0, encryptedHeaderData.Length - GetEncryptedHeaderPartOffset());
    }

    protected void EncryptHeader(byte[] headerData, byte[] key)
    {
        EncEngine!.SetKey(key);
        EncEngine.Init();
        EncEngine.SetIV(new byte[EncEngine.IVSize]);
        int encOffs = GetEncryptedHeaderPartOffset();
        EncEngine.Encrypt(headerData, encOffs, headerData.Length - encOffs);
    }

    /// <summary>Prepares a fresh container of the given total size in a base IO.</summary>
    public void FormatNew(IRandomAccessIO output, long containerSize)
    {
        SetContainerSize(containerSize);
        InitNew();
        output.SetLength(containerSize);
        WriteHeader(output);
    }

    protected virtual long CalcVolumeSize(long containerSize)
    {
        long numSectors = containerSize / SectorSize;
        return (containerSize % SectorSize == 0 ? numSectors : numSectors + 1) * SectorSize - ReservedHeaderSize;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes (or fewer at EOF).</summary>
    protected static int ReadBytes(IRandomAccessIO io, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = io.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
