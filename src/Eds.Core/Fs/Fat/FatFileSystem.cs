using System.Text;

namespace Eds.Core.Fs.Fat;

/// <summary>
/// Read-oriented FAT12/16/32 driver. Free-form port of the read path of
/// <c>fs.fat.FatFS</c>: mounts over any <see cref="IRandomAccessIO"/> (typically
/// an <see cref="Eds.Core.Crypto.EncryptedFile"/> exposing a decrypted volume),
/// lists directories (with LFN), resolves paths and opens files as streams that
/// follow the cluster chain.
///
/// Write support (creating/deleting files, cluster allocation, FSInfo updates)
/// is the next increment - see docs/ROADMAP.md.
/// </summary>
public sealed partial class FatFileSystem
{
    private readonly IRandomAccessIO _io;
    private readonly Bpb _bpb;
    private readonly FatTable _fat;

    private FatFileSystem(IRandomAccessIO io, Bpb bpb)
    {
        _io = io;
        _bpb = bpb;
        _fat = new FatTable(io, bpb);
    }

    public FatType Type => _bpb.Type;
    public int ClusterSize => _bpb.ClusterSize;
    public long TotalSize => _bpb.TotalSectors * _bpb.BytesPerSector;

    /// <summary>Mounts a FAT volume. Throws if the image is not FAT.</summary>
    public static FatFileSystem Mount(IRandomAccessIO io)
    {
        var bpb = Bpb.Read(io);
        return new FatFileSystem(io, bpb);
    }

    /// <summary>Lists the root directory.</summary>
    public IReadOnlyList<FatDirEntry> ListRoot()
    {
        byte[] data = _bpb.Type == FatType.Fat32
            ? ReadClusterChain(_bpb.RootClusterNumber)
            : ReadFixedRootDir();
        return FatDirEntry.ParseDirectory(data).ToList();
    }

    /// <summary>Lists a subdirectory entry.</summary>
    public IReadOnlyList<FatDirEntry> ListDirectory(FatDirEntry dir)
    {
        if (!dir.IsDirectory) throw new InvalidOperationException($"{dir.Name} is not a directory");
        byte[] data = ReadClusterChain(dir.FirstCluster);
        return FatDirEntry.ParseDirectory(data).ToList();
    }

    /// <summary>
    /// Resolves a slash-separated path (e.g. "/docs/readme.txt") to an entry, or
    /// null if any component is missing. Comparison is case-insensitive.
    /// </summary>
    public FatDirEntry? ResolvePath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<FatDirEntry> current = ListRoot();
        FatDirEntry? entry = null;
        foreach (var part in parts)
        {
            entry = current.FirstOrDefault(e =>
                string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            if (entry.IsDirectory) current = ListDirectory(entry);
        }
        return entry;
    }

    /// <summary>Opens a file entry as a read-only random-access stream.</summary>
    public IRandomAccessIO OpenFile(FatDirEntry file)
    {
        if (file.IsDirectory) throw new InvalidOperationException($"{file.Name} is a directory");
        var clusters = _fat.Chain(file.FirstCluster).ToList();
        return new FatFileReader(_io, _bpb, clusters, file.Size);
    }

    /// <summary>Reads a whole file into memory (convenience for small files).</summary>
    public byte[] ReadAllBytes(FatDirEntry file)
    {
        using var s = OpenFile(file);
        var buf = new byte[file.Size];
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf, total, buf.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return buf;
    }

    private byte[] ReadFixedRootDir()
    {
        int len = _bpb.RootDirEntries * 32;
        var data = new byte[len];
        _io.Seek(_bpb.RootDirOffset);
        IoUtil.ReadBytes(_io, data, len);
        return data;
    }

    private byte[] ReadClusterChain(long firstCluster)
    {
        var clusters = _fat.Chain(firstCluster).ToList();
        int cs = _bpb.ClusterSize;
        var data = new byte[clusters.Count * cs];
        int pos = 0;
        foreach (var c in clusters)
        {
            _io.Seek(_bpb.GetClusterOffset(c));
            IoUtil.ReadBytes(_io, data, pos, cs);
            pos += cs;
        }
        return data;
    }

    // ===================== write operations ============================

    /// <summary>
    /// Writes a file into the root directory (short 8.3 name, e.g. "READ.TXT").
    /// Allocates a cluster chain, writes the data and adds a directory entry.
    /// Supports FAT12/16 (fixed root region). Long names on write are a later
    /// increment - names are upper-cased and truncated to 8.3.
    /// </summary>
    public void WriteFileToRoot(string name, byte[] content)
    {
        if (_bpb.Type == FatType.Fat32)
            throw new NotSupportedException("FAT32 root-write not yet implemented");

        var (baseName, ext) = ToShortName(name);
        long firstCluster = WriteContentClusters(content);

        int slot = FindFreeRootSlot();
        if (slot < 0) throw new IOException("Root directory is full");

        var entry = new byte[FatDirEntry.RecordSize];
        Encoding.ASCII.GetBytes(baseName).CopyTo(entry, 0);
        Encoding.ASCII.GetBytes(ext).CopyTo(entry, 8);
        entry[11] = (byte)FatAttr.Archive;
        // first cluster low (FAT16/12: high word stays 0)
        entry[26] = (byte)(firstCluster & 0xff);
        entry[27] = (byte)((firstCluster >> 8) & 0xff);
        long size = content.Length;
        entry[28] = (byte)(size & 0xff);
        entry[29] = (byte)((size >> 8) & 0xff);
        entry[30] = (byte)((size >> 16) & 0xff);
        entry[31] = (byte)((size >> 24) & 0xff);

        _io.Seek(_bpb.RootDirOffset + (long)slot * FatDirEntry.RecordSize);
        _io.Write(entry, 0, entry.Length);
        _io.Flush();
    }

    private long WriteContentClusters(byte[] content)
    {
        int cs = _bpb.ClusterSize;
        int numClusters = content.Length == 0 ? 1 : (content.Length + cs - 1) / cs;

        long first = -1, prev = -1;
        var buf = new byte[cs];
        for (int i = 0; i < numClusters; i++)
        {
            long c = _fat.AllocateCluster(_bpb.CountOfClusters);
            if (c < 0) throw new IOException("Volume is full");
            if (first < 0) first = c;
            if (prev >= 0) _fat.SetEntry(prev, c); // link previous -> current

            Array.Clear(buf);
            int chunk = Math.Min(cs, content.Length - i * cs);
            if (chunk > 0) Array.Copy(content, i * cs, buf, 0, chunk);
            _io.Seek(_bpb.GetClusterOffset(c));
            _io.Write(buf, 0, cs);

            prev = c;
        }
        // last cluster already EOC from AllocateCluster
        return first;
    }

    private int FindFreeRootSlot()
    {
        int max = _bpb.RootDirEntries;
        var rec = new byte[FatDirEntry.RecordSize];
        for (int i = 0; i < max; i++)
        {
            _io.Seek(_bpb.RootDirOffset + (long)i * FatDirEntry.RecordSize);
            IoUtil.ReadBytes(_io, rec, FatDirEntry.RecordSize);
            if (rec[0] == 0x00 || rec[0] == 0xE5) return i;
        }
        return -1;
    }

    private static (string BaseName, string Ext) ToShortName(string name)
    {
        name = name.Trim().ToUpperInvariant();
        string b, e;
        int dot = name.LastIndexOf('.');
        if (dot >= 0) { b = name[..dot]; e = name[(dot + 1)..]; }
        else { b = name; e = ""; }
        b = SanitizeShort(b, 8);
        e = SanitizeShort(e, 3);
        return (b.PadRight(8), e.PadRight(3));
    }

    private static string SanitizeShort(string s, int max)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s)
        {
            if (sb.Length >= max) break;
            sb.Append(ch is ' ' or '.' ? '_' : ch);
        }
        return sb.ToString();
    }
}
