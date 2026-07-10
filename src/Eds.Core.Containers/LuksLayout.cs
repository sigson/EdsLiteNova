using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Eds.Core.Crypto;
using Eds.Core.Crypto.Engines;
using Eds.Core.Crypto.Hash;
using Eds.Core.Exceptions;
using Eds.Core.Fs;

namespace Eds.Core.Containers;

/// <summary>
/// LUKS1 volume layout. Faithful port of <c>luks.VolumeLayout</c> (big-endian
/// header, 8 key slots, AF-split master key). Read path opens a container by
/// trying each active key slot; write path (<see cref="FormatNew"/>) creates one
/// so the whole flow can be round-tripped without cryptsetup.
///
/// As in the original, only the modes edslite exposes are supported
/// (xts-plain64, cbc-plain); ESSIV LUKS volumes are out of scope.
/// </summary>
public sealed class LuksLayout : VolumeLayoutBase
{
    private const int NumKeySlots = 8;
    private const int KeyDisabledSig = 0x0000DEAD;
    private const int KeyEnabledSig = 0x00AC71F3;
    private const int MaxCipherNameLen = 32;
    private const int MaxCipherModeNameLen = 32;
    private const int MaxHashSpecLen = 32;
    private const int MkSaltSize = 32;
    private const int MkIterationsMin = 1000;
    private const int SlotIterationsMin = 5000;
    private const int MkDigestSize = 20;
    private const int UuidLength = 40;
    private const int HeaderSize = 1024;
    private const int KeyMaterialOffset = 4096;
    private const int NumAfStripes = 4000;
    private const int DefaultDiskAlignment = 1024 * 1024;

    private static readonly byte[] Magic = { (byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xba, 0xbe };

    private Guid _uuid;
    private int _payloadOffsetSector;
    private int _activeKeyslotIndex;
    private long _volumeSize;
    private readonly List<KeySlot> _keySlots = new();

    public override long EncryptedDataOffset => (long)_payloadOffsetSector * SectorSize;
    public override long GetEncryptedDataSize(long fileSize) => _volumeSize;

    // LUKS keys the payload IV from the *volume* offset (no header offset added).
    public override void SetEncryptionEngineIV(IFileEncryptionEngine eng, long decryptedVolumeOffset)
    {
        long block = decryptedVolumeOffset / eng.FileBlockSize;
        eng.SetIV(GetIVFromBlockIndex(block));
    }

    public override IReadOnlyList<IFileEncryptionEngine> GetSupportedEncryptionEngines() => new IFileEncryptionEngine[]
    {
        new AesXts(), new SerpentXts(), new TwofishXts(), new GostXts(),
        new AesCbc(), new SerpentCbc(), new TwofishCbc(),
    };

    public override IReadOnlyList<IMessageDigest> GetSupportedHashFuncs() => new IMessageDigest[]
    {
        BclDigest.Sha1(), BclDigest.Sha512(), BclDigest.Sha256(), new Ripemd160(), new Whirlpool(),
    };

    private IMessageDigest? FindHashFunc(string name)
    {
        name = name.Trim().ToLowerInvariant();
        return name switch
        {
            "sha1" or "sha-1" => BclDigest.Sha1(),
            "sha256" or "sha-256" => BclDigest.Sha256(),
            "sha512" or "sha-512" => BclDigest.Sha512(),
            "ripemd160" => new Ripemd160(),
            "whirlpool" => new Whirlpool(),
            _ => null,
        };
    }

    private IFileEncryptionEngine? FindCipher(string cipherName, string modeName)
    {
        cipherName = cipherName.Trim().ToLowerInvariant();
        modeName = modeName.Trim().ToLowerInvariant();
        bool xts = modeName.Contains("xts");
        bool cbc = modeName.Contains("cbc");
        return (cipherName, xts, cbc) switch
        {
            ("aes", true, _) => new AesXts(),
            ("serpent", true, _) => new SerpentXts(),
            ("twofish", true, _) => new TwofishXts(),
            ("gost", true, _) => new GostXts(),
            ("aes", _, true) => new AesCbc(),
            ("serpent", _, true) => new SerpentCbc(),
            ("twofish", _, true) => new TwofishCbc(),
            _ => null,
        };
    }

    // ---- read ---------------------------------------------------------
    public override bool ReadHeader(IRandomAccessIO input)
    {
        CheckReadHeaderPrereqs();
        var header = new byte[HeaderSize];
        input.Seek(0);
        if (IoUtil.ReadBytes(input, header, HeaderSize) != HeaderSize) return false;
        for (int i = 0; i < Magic.Length; i++)
            if (header[i] != Magic[i]) return false;

        var mki = DeserializeHeaderData(header);
        for (int i = 0; i < _keySlots.Count; i++)
        {
            var ks = _keySlots[i];
            if (!ks.IsActive) continue;
            if (TryPassword(input, ks, mki))
            {
                _activeKeyslotIndex = i;
                _volumeSize = input.Length() - (long)_payloadOffsetSector * SectorSize;
                return true;
            }
        }
        throw new WrongPasswordException();
    }

    private bool TryPassword(IRandomAccessIO io, KeySlot ks, MkInfo mki)
    {
        io.Seek((long)ks.KeyMaterialOffsetSector * SectorSize);
        var af = new Af(HashFunc!, mki.KeyLength);
        int afSize = af.CalcNumRequiredSectors(ks.NumStripes) * SectorSize;
        var afKey = new byte[afSize];
        if (IoUtil.ReadBytes(io, afKey, afSize) != afSize) throw new EndOfStreamException();

        OpeningProgressReporter?.SetCurrentKdfName(HashFunc!.Algorithm);
        OpeningProgressReporter?.SetCurrentEncryptionAlgName(Engine.CipherName);

        byte[] key = DeriveKey(Engine.KeySize, HashFunc!, Password!, ks.Salt, ks.PasswordIterations);
        Engine.SetKey(key);
        Engine.Init();
        Engine.SetIncrementIV(true); // key material uses per-sector IVs (matters for CBC)
        Engine.SetIV(new byte[Engine.IVSize]);
        Engine.Decrypt(afKey, 0, afKey.Length);

        var mk = new byte[mki.KeyLength];
        af.Merge(afKey, 0, mk, 0, ks.NumStripes);

        if (mki.IsValidKey(this, mk))
        {
            if (MasterKey != null) Array.Clear(MasterKey);
            MasterKey = mk;
            Engine.SetKey(MasterKey);
            Engine.Init();
            return true;
        }
        Array.Clear(mk);
        return false;
    }

    private MkInfo DeserializeHeaderData(byte[] header)
    {
        var c = new BeCursor(header, Magic.Length);
        short ver = c.GetInt16();
        if (ver > 1) throw new UnsupportedContainerTypeException();

        string cipherName = c.GetString(MaxCipherNameLen);
        string modeName = c.GetString(MaxCipherModeNameLen);
        string hashName = c.GetString(MaxHashSpecLen);

        HashFunc = FindHashFunc(hashName) ?? throw new EdsException($"Unsupported hash: {hashName}");
        _payloadOffsetSector = c.GetInt32();

        var mki = new MkInfo();
        mki.Deserialize(c);

        SetEngine(FindCipher(cipherName, modeName) ?? throw new EdsException($"Unsupported cipher: {cipherName}-{modeName}"));

        string uuid = c.GetString(UuidLength);
        Guid.TryParse(uuid, out _uuid);

        _keySlots.Clear();
        for (int i = 0; i < NumKeySlots; i++)
        {
            var ks = new KeySlot();
            ks.Deserialize(c);
            _keySlots.Add(ks);
        }
        return mki;
    }

    // ---- write (create) ----------------------------------------------
    public override void InitNew()
    {
        if (EncEngine == null) SetEngine(new AesXts());
        base.InitNew(); // random master key of Engine.KeySize
        HashFunc ??= BclDigest.Sha1();
        _activeKeyslotIndex = 0;
        if (_uuid == Guid.Empty) _uuid = Guid.NewGuid();

        _keySlots.Clear();
        for (int i = 0; i < NumKeySlots; i++)
        {
            var ks = new KeySlot();
            ks.Init(this, i);
            _keySlots.Add(ks);
        }
        if (_payloadOffsetSector == 0)
        {
            var probe = new KeySlot();
            probe.Init(this, NumKeySlots);
            _payloadOffsetSector = SizeRoundUp(probe.KeyMaterialOffsetSector, DefaultDiskAlignment / SectorSize);
        }
    }

    public void FormatNew(IRandomAccessIO output, long volumeSize)
    {
        InitNew();
        _volumeSize = volumeSize;
        long total = (long)_payloadOffsetSector * SectorSize + volumeSize;
        output.SetLength(total);
        WriteKey(output, _keySlots[_activeKeyslotIndex]);
        WriteHeaderData(output);
        Engine.SetKey(MasterKey);
        Engine.Init();
    }

    /// <summary>
    /// Re-keys the keyslot that opened this volume with a new password, keeping the
    /// master key (so the payload stays readable). The container must already be
    /// open. Port of the LUKS side of <c>ChangeContainerPasswordTask</c> — the
    /// slot's key material is regenerated (fresh salt, AF-split of the master key
    /// under the new password) and the header rewritten with a fresh master-key
    /// digest salt. The old password no longer opens the slot.
    /// </summary>
    public void ChangePassword(byte[] newPassword, IRandomAccessIO output)
    {
        if (MasterKey == null || HashFunc == null || EncEngine == null)
            throw new InvalidOperationException("Container is not open");

        var ks = _keySlots[_activeKeyslotIndex];
        RandomNumberGenerator.Fill(ks.Salt); // fresh keyslot salt
        SetPassword(newPassword);            // layout owns + clears on dispose

        WriteKey(output, ks);        // AF-split the master key under the new password
        WriteHeaderData(output);     // rewrite header (fresh MK-digest salt + keyslots)

        Engine.SetKey(MasterKey);    // restore the payload engine to the master key
        Engine.Init();
    }

    private void WriteKey(IRandomAccessIO output, KeySlot ks)
    {
        byte[] derivedKey = DeriveKey(Engine.KeySize, HashFunc!, Password!, ks.Salt, ks.PasswordIterations);
        var af = new Af(HashFunc!, derivedKey.Length);
        int afSize = af.CalcNumRequiredSectors(ks.NumStripes) * Af.SectorSize;
        var afKey = new byte[afSize];
        af.Split(MasterKey!, 0, afKey, 0, ks.NumStripes);

        Engine.SetKey(derivedKey);
        Engine.Init();
        Engine.SetIncrementIV(true); // key material uses per-sector IVs (matters for CBC)
        Engine.SetIV(new byte[Engine.IVSize]);
        Engine.Encrypt(afKey, 0, afKey.Length);

        output.Seek((long)ks.KeyMaterialOffsetSector * SectorSize);
        output.Write(afKey, 0, afKey.Length);
        ks.IsActive = true;

        Array.Clear(derivedKey);
        Array.Clear(afKey);
    }

    private void WriteHeaderData(IRandomAccessIO output)
    {
        var header = new byte[HeaderSize];
        var c = new BeCursor(header, 0);
        c.PutBytes(Magic);
        c.PutInt16(1);
        c.PutString(Engine.CipherName.ToLowerInvariant(), MaxCipherNameLen);
        c.PutString(Engine.CipherModeName, MaxCipherModeNameLen);
        c.PutString(HashSpecName(), MaxHashSpecLen);
        c.PutInt32(_payloadOffsetSector);

        var mki = new MkInfo();
        mki.Init(this);
        mki.Serialize(c);

        c.PutString(_uuid.ToString(), UuidLength);
        foreach (var ks in _keySlots) ks.Serialize(c);

        output.Seek(0);
        output.Write(header, 0, header.Length);
    }

    private string HashSpecName()
    {
        var a = HashFunc!.Algorithm.ToLowerInvariant();
        return a switch { "sha-512" => "sha512", "sha-256" => "sha256", "sha-1" => "sha1", _ => a };
    }

    private static int SizeRoundUp(int size, int block) => (size + block - 1) / block * block;

    // ---- nested types -------------------------------------------------
    private sealed class KeySlot
    {
        public bool IsActive;
        public int PasswordIterations;
        public byte[] Salt = new byte[MkSaltSize];
        public int KeyMaterialOffsetSector;
        public int NumStripes;

        public void Init(LuksLayout owner, int slotIndex)
        {
            IsActive = false;
            PasswordIterations = SlotIterationsMin;
            Salt = new byte[MkSaltSize];
            RandomNumberGenerator.Fill(Salt);
            NumStripes = NumAfStripes;
            var af = new Af(owner.HashFunc!, owner.MasterKey!.Length);
            int blocksPerStripeSet = af.CalcNumRequiredSectors(NumStripes);
            int sector = KeyMaterialOffset / SectorSize;
            for (int i = 0; i < slotIndex; i++)
                sector = SizeRoundUp(sector + blocksPerStripeSet, KeyMaterialOffset / SectorSize);
            KeyMaterialOffsetSector = sector;
        }

        public void Serialize(BeCursor c)
        {
            c.PutInt32(IsActive ? KeyEnabledSig : KeyDisabledSig);
            c.PutInt32(PasswordIterations);
            c.PutBytes(Salt);
            c.PutInt32(KeyMaterialOffsetSector);
            c.PutInt32(NumStripes);
        }

        public void Deserialize(BeCursor c)
        {
            IsActive = c.GetInt32() == KeyEnabledSig;
            PasswordIterations = c.GetInt32();
            Salt = c.GetBytes(MkSaltSize);
            KeyMaterialOffsetSector = c.GetInt32();
            NumStripes = c.GetInt32();
        }
    }

    private sealed class MkInfo
    {
        public int Iterations;
        public int KeyLength;
        public byte[] Salt = new byte[MkSaltSize];
        public byte[] Digest = new byte[MkDigestSize];

        public void Init(LuksLayout owner)
        {
            Iterations = MkIterationsMin;
            KeyLength = owner.MasterKey!.Length;
            Salt = new byte[MkSaltSize];
            RandomNumberGenerator.Fill(Salt);
            Digest = owner.DeriveKey(MkDigestSize, owner.HashFunc!, owner.MasterKey!, Salt, Iterations);
        }

        public bool IsValidKey(LuksLayout owner, byte[] key)
        {
            var d = owner.DeriveKey(MkDigestSize, owner.HashFunc!, key, Salt, Iterations);
            return CryptographicOperations.FixedTimeEquals(d, Digest);
        }

        public void Serialize(BeCursor c)
        {
            c.PutInt32(KeyLength);
            c.PutBytes(Digest);
            c.PutBytes(Salt);
            c.PutInt32(Iterations);
        }

        public void Deserialize(BeCursor c)
        {
            KeyLength = c.GetInt32();
            Digest = c.GetBytes(MkDigestSize);
            Salt = c.GetBytes(MkSaltSize);
            Iterations = c.GetInt32();
        }
    }

    /// <summary>Sequential big-endian read/write cursor over a byte buffer.</summary>
    private sealed class BeCursor
    {
        private readonly byte[] _buf;
        private int _pos;
        public BeCursor(byte[] buf, int pos) { _buf = buf; _pos = pos; }

        public short GetInt16() { short v = BinaryPrimitives.ReadInt16BigEndian(_buf.AsSpan(_pos, 2)); _pos += 2; return v; }
        public int GetInt32() { int v = BinaryPrimitives.ReadInt32BigEndian(_buf.AsSpan(_pos, 4)); _pos += 4; return v; }
        public byte[] GetBytes(int n) { var b = new byte[n]; Array.Copy(_buf, _pos, b, 0, n); _pos += n; return b; }
        public string GetString(int n)
        {
            var b = GetBytes(n);
            int len = Array.IndexOf(b, (byte)0);
            if (len < 0) len = n;
            return Encoding.ASCII.GetString(b, 0, len).Trim();
        }

        public void PutInt16(short v) { BinaryPrimitives.WriteInt16BigEndian(_buf.AsSpan(_pos, 2), v); _pos += 2; }
        public void PutInt32(int v) { BinaryPrimitives.WriteInt32BigEndian(_buf.AsSpan(_pos, 4), v); _pos += 4; }
        public void PutBytes(byte[] b) { Array.Copy(b, 0, _buf, _pos, b.Length); _pos += b.Length; }
        public void PutString(string s, int field)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            int n = Math.Min(bytes.Length, field);
            Array.Copy(bytes, 0, _buf, _pos, n);
            _pos += field; // remaining bytes stay zero
        }
    }
}
