namespace Eds.Core.Containers;

/// <summary>
/// Mixes TrueCrypt/VeraCrypt keyfiles into a password. Port of the keyfile pool
/// algorithm (<c>Keyfile::Apply</c> + <c>VolumePassword</c> keyfile combination):
/// a 64-byte pool is built from the running CRC of every keyfile byte and then
/// added, byte-wise, into the first 64 bytes of the password (which is extended to
/// 64 bytes if shorter).
///
/// <para><b>CRC note.</b> The keyfile CRC is TrueCrypt's <em>non-reflected</em>
/// CRC-32 (polynomial 0x04C11DB7, MSB-first, init 0xFFFFFFFF, no final xor) — this
/// is deliberately NOT the reflected zip/PNG CRC-32 in
/// <c>System.IO.Hashing.Crc32</c>. Using the wrong variant would make containers
/// created with keyfiles by real TrueCrypt/VeraCrypt impossible to open, so the
/// table is generated explicitly here.</para>
///
/// <para><b>Compatibility.</b> This is a faithful implementation of the documented
/// algorithm; byte-exact parity against volumes produced by real VeraCrypt should
/// be confirmed with reference artifacts (cross-task K1). The create/open
/// round-trip test proves internal consistency.</para>
/// </summary>
public static class KeyfileMixer
{
    public const int PoolSize = 64;

    private static readonly uint[] Crc32Table = BuildTable();

    /// <summary>
    /// The exact CRC variant the keyfile pool uses: CRC-32/MPEG-2 (poly 0x04C11DB7,
    /// init 0xFFFFFFFF, non-reflected, no final xor) — returned as the raw running
    /// register. Exposed so a KAT can pin it to the published check value
    /// (CRC of "123456789" == 0x0376E6E7), which validates the table byte-for-byte.
    /// </summary>
    public static uint Crc32Mpeg2(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = Crc32Table[((crc >> 24) ^ b) & 0xFF] ^ (crc << 8);
        return crc;
    }

    /// <summary>
    /// Returns a new effective password with the keyfiles mixed in. When
    /// <paramref name="keyfiles"/> is null/empty the password is returned unchanged
    /// (a clone), so passing no keyfiles is a no-op — matching TrueCrypt/VeraCrypt.
    /// </summary>
    public static byte[] Apply(byte[] password, IReadOnlyList<Func<Stream>>? keyfiles)
    {
        if (keyfiles == null || keyfiles.Count == 0)
            return (byte[])password.Clone();

        var pool = new byte[PoolSize];
        try
        {
            foreach (var open in keyfiles)
            {
                using var s = open();
                MixKeyfile(pool, s);
            }
            return Combine(password, pool);
        }
        finally
        {
            Array.Clear(pool);
        }
    }

    private static void MixKeyfile(byte[] pool, Stream s)
    {
        uint crc = 0xFFFFFFFF;
        int poolPos = 0;
        var buf = new byte[64 * 1024];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                crc = Crc32Table[((crc >> 24) ^ buf[i]) & 0xFF] ^ (crc << 8);

                pool[poolPos++] += (byte)(crc >> 24); if (poolPos >= PoolSize) poolPos = 0;
                pool[poolPos++] += (byte)(crc >> 16); if (poolPos >= PoolSize) poolPos = 0;
                pool[poolPos++] += (byte)(crc >> 8); if (poolPos >= PoolSize) poolPos = 0;
                pool[poolPos++] += (byte)crc; if (poolPos >= PoolSize) poolPos = 0;
            }
        }
        Array.Clear(buf);
    }

    private static byte[] Combine(byte[] password, byte[] pool)
    {
        int newLen = Math.Max(password.Length, PoolSize);
        var res = new byte[newLen];
        Array.Copy(password, res, password.Length);
        for (int i = 0; i < PoolSize; i++)
            res[i] = (byte)(res[i] + pool[i]); // modular byte addition
        return res;
    }

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++)
                c = (c & 0x80000000u) != 0 ? (c << 1) ^ 0x04C11DB7u : c << 1;
            t[i] = c;
        }
        return t;
    }
}

/// <summary>Builds keyfile stream factories from common sources.</summary>
public static class Keyfiles
{
    /// <summary>A keyfile read from a file path.</summary>
    public static Func<Stream> FromFile(string path)
        => () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>A keyfile from in-memory bytes.</summary>
    public static Func<Stream> FromBytes(byte[] data)
        => () => new MemoryStream(data, writable: false);
}
