using System.Text;

namespace Eds.Core.Fs.Fat;

public sealed partial class FatFileSystem
{
    // Reference to a directory: either the fixed FAT12/16 root region, or a
    // cluster-chain directory (FAT32 root or any subdirectory) identified by its
    // first cluster.
    private readonly struct DirRef
    {
        public readonly bool IsFixedRoot;
        public readonly long FirstCluster;
        private DirRef(bool fixedRoot, long firstCluster) { IsFixedRoot = fixedRoot; FirstCluster = firstCluster; }
        public static DirRef FixedRoot => new(true, 0);
        public static DirRef Cluster(long c) => new(false, c);
        public long DotDotCluster => IsFixedRoot ? 0 : FirstCluster;
    }

    private DirRef RootRef => _bpb.Type == FatType.Fat32
        ? DirRef.Cluster(_bpb.RootClusterNumber)
        : DirRef.FixedRoot;

    private int EntriesPerCluster => _bpb.ClusterSize / FatDirEntry.RecordSize;

    // ---- public path-based operations ---------------------------------

    /// <summary>Writes a file at the given path (creating LFN entries as needed).</summary>
    public void WriteFile(string path, byte[] content)
    {
        var (dir, leaf) = ResolveParent(path);
        long firstCluster = content.Length == 0 ? 0 : WriteContentClusters(content);
        AddEntry(dir, leaf, FatAttr.Archive, firstCluster, content.Length);
    }

    /// <summary>Creates a directory at the given path.</summary>
    public void CreateDirectory(string path)
    {
        var (parent, leaf) = ResolveParent(path);

        long cluster = _fat.AllocateCluster(_bpb.CountOfClusters);
        if (cluster < 0) throw new IOException("Volume is full");
        ZeroCluster(cluster);

        // "." and ".." entries at the start of the new directory
        WriteRawRecord(DirRef.Cluster(cluster), 0, ShortEntry(".", FatAttr.Directory, cluster, 0));
        WriteRawRecord(DirRef.Cluster(cluster), 1, ShortEntry("..", FatAttr.Directory, parent.DotDotCluster, 0));

        AddEntry(parent, leaf, FatAttr.Directory, cluster, 0);
    }

    /// <summary>Deletes a file or (empty) directory at the given path.</summary>
    public void Delete(string path)
    {
        var (parent, leaf) = ResolveParent(path);
        var (startSlot, count, entry) = FindEntrySlots(parent, leaf)
            ?? throw new FileNotFoundException(path);

        if (entry.IsDirectory && !IsDirectoryEmpty(entry.FirstCluster))
            throw new IOException("Directory is not empty");

        // free the cluster chain
        if (entry.FirstCluster >= 2)
            foreach (var c in _fat.Chain(entry.FirstCluster).ToList())
                _fat.SetEntry(c, 0);

        MarkSlotsDeleted(parent, startSlot, count);
        _io.Flush();
    }

    /// <summary>
    /// Moves/renames an entry: adds a directory entry at <paramref name="toPath"/>
    /// pointing at the same data, then removes the source entry. No file data is
    /// copied (only directory records change).
    /// </summary>
    public void Move(string fromPath, string toPath)
    {
        var (srcDir, srcLeaf) = ResolveParent(fromPath);
        var found = FindEntrySlots(srcDir, srcLeaf) ?? throw new FileNotFoundException(fromPath);
        var (dstDir, dstLeaf) = ResolveParent(toPath);

        // create the new entry pointing at the same first cluster / size
        AddEntry(dstDir, dstLeaf, found.entry.Attributes, found.entry.FirstCluster, found.entry.Size);

        // moving a directory to a different parent: fix its ".." to the new parent
        if (found.entry.IsDirectory && found.entry.FirstCluster >= 2)
            WriteRawRecord(DirRef.Cluster(found.entry.FirstCluster), 1,
                ShortEntry("..", FatAttr.Directory, dstDir.DotDotCluster, 0));

        MarkSlotsDeleted(srcDir, found.start, found.count);
        _io.Flush();
    }

    /// <summary>Renames an entry within its current directory.</summary>
    public void Rename(string path, string newName)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Empty path");
        parts[^1] = newName;
        Move(path, "/" + string.Join("/", parts));
    }

    private void MarkSlotsDeleted(DirRef dir, int startSlot, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var rec = new byte[FatDirEntry.RecordSize];
            ReadRawRecord(dir, startSlot + i, rec);
            rec[0] = 0xE5;
            WriteRawRecord(dir, startSlot + i, rec);
        }
    }

    // ---- directory listing by ref -------------------------------------

    private byte[] ReadDirBytes(DirRef dir)
    {
        if (dir.IsFixedRoot) return ReadFixedRootDir();
        return ReadClusterChain(dir.FirstCluster);
    }

    private IReadOnlyList<FatDirEntry> ListDir(DirRef dir) =>
        FatDirEntry.ParseDirectory(ReadDirBytes(dir)).ToList();

    private (DirRef dir, string leaf) ResolveParent(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Empty path");
        var dir = RootRef;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var entry = ListDir(dir).FirstOrDefault(e =>
                e.IsDirectory && string.Equals(e.Name, parts[i], StringComparison.OrdinalIgnoreCase))
                ?? throw new DirectoryNotFoundException(parts[i]);
            dir = DirRef.Cluster(entry.FirstCluster);
        }
        return (dir, parts[^1]);
    }

    private bool IsDirectoryEmpty(long firstCluster)
    {
        foreach (var e in ListDir(DirRef.Cluster(firstCluster)))
            if (e.Name is not "." and not "..") return false;
        return true;
    }

    // ---- raw record IO within a directory -----------------------------

    private long RecordOffset(DirRef dir, int index, bool allocateIfNeeded)
    {
        if (dir.IsFixedRoot)
        {
            if (index >= _bpb.RootDirEntries) return -1;
            return _bpb.RootDirOffset + (long)index * FatDirEntry.RecordSize;
        }

        int epc = EntriesPerCluster;
        int clusterIdx = index / epc;
        int within = index % epc;

        var chain = _fat.Chain(dir.FirstCluster).ToList();
        while (clusterIdx >= chain.Count)
        {
            if (!allocateIfNeeded) return -1;
            long nc = _fat.AllocateCluster(_bpb.CountOfClusters);
            if (nc < 0) throw new IOException("Volume is full");
            _fat.SetEntry(chain[^1], nc);
            ZeroCluster(nc);
            chain.Add(nc);
        }
        return _bpb.GetClusterOffset(chain[clusterIdx]) + (long)within * FatDirEntry.RecordSize;
    }

    private bool ReadRawRecord(DirRef dir, int index, byte[] rec)
    {
        long off = RecordOffset(dir, index, allocateIfNeeded: false);
        if (off < 0) return false;
        _io.Seek(off);
        return IoUtil.ReadBytes(_io, rec, FatDirEntry.RecordSize) == FatDirEntry.RecordSize;
    }

    private void WriteRawRecord(DirRef dir, int index, byte[] rec)
    {
        long off = RecordOffset(dir, index, allocateIfNeeded: true);
        if (off < 0) throw new IOException("Directory is full");
        _io.Seek(off);
        _io.Write(rec, 0, FatDirEntry.RecordSize);
    }

    // ---- adding entries (with LFN) ------------------------------------

    private void AddEntry(DirRef dir, string name, FatAttr attr, long firstCluster, long size)
    {
        var shortName = MakeUniqueShortName(dir, name, out bool needsLfn);
        var records = new List<byte[]>();

        if (needsLfn)
        {
            byte checksum = ShortNameChecksum(shortName);
            var chunks = SplitLfn(name);
            // physical order: highest sequence (with 0x40) first
            for (int seq = chunks.Count; seq >= 1; seq--)
            {
                bool last = seq == chunks.Count;
                records.Add(LfnEntry(chunks[seq - 1], seq, last, checksum));
            }
        }
        records.Add(ShortEntryRaw(shortName, attr, firstCluster, size));

        int start = FindFreeRun(dir, records.Count);
        for (int i = 0; i < records.Count; i++)
            WriteRawRecord(dir, start + i, records[i]);
        _io.Flush();
    }

    // Finds `count` consecutive free slots, extending a cluster directory if needed.
    private int FindFreeRun(DirRef dir, int count)
    {
        int run = 0, runStart = 0, index = 0;
        var rec = new byte[FatDirEntry.RecordSize];
        while (true)
        {
            if (!ReadRawRecord(dir, index, rec))
            {
                // physical end of a cluster dir; WriteRawRecord will allocate more.
                int start0 = run > 0 ? runStart : index;
                if (dir.IsFixedRoot && start0 + count > _bpb.RootDirEntries)
                    throw new IOException("Root directory is full");
                return start0;
            }
            if (rec[0] == 0x00)
            {
                // end marker: this slot and everything after it are free
                int start = run > 0 ? runStart : index;
                if (dir.IsFixedRoot && start + count > _bpb.RootDirEntries)
                    throw new IOException("Root directory is full");
                return start;
            }
            if (rec[0] == 0xE5)
            {
                if (run == 0) runStart = index;
                if (++run >= count) return runStart;
            }
            else run = 0;

            index++;
            if (dir.IsFixedRoot && index >= _bpb.RootDirEntries)
                throw new IOException("Root directory is full");
        }
    }

    private (int start, int count, FatDirEntry entry)? FindEntrySlots(DirRef dir, string name)
    {
        int index = 0;
        var rec = new byte[FatDirEntry.RecordSize];
        var lfn = new List<byte[]>();
        int runStart = -1;
        while (ReadRawRecord(dir, index, rec))
        {
            if (rec[0] == 0x00) break;
            if (rec[0] == 0xE5) { lfn.Clear(); runStart = -1; index++; continue; }

            var attr = (FatAttr)rec[11];
            if ((attr & FatAttr.LongName) == FatAttr.LongName)
            {
                if (runStart < 0) runStart = index;
                lfn.Add((byte[])rec.Clone());
                index++;
                continue;
            }

            // short entry — assemble the display name via the shared parser
            var seq = new List<byte[]>(lfn) { (byte[])rec.Clone() };
            var full = new byte[seq.Count * FatDirEntry.RecordSize];
            for (int i = 0; i < seq.Count; i++)
                Array.Copy(seq[i], 0, full, i * FatDirEntry.RecordSize, FatDirEntry.RecordSize);
            var entry = FatDirEntry.ParseDirectory(full).FirstOrDefault();

            if (entry != null && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                int start = lfn.Count > 0 ? runStart : index;
                int count = index - start + 1;
                return (start, count, entry);
            }
            lfn.Clear(); runStart = -1;
            index++;
        }
        return null;
    }

    // ---- short/long name helpers --------------------------------------

    private string MakeUniqueShortName(DirRef dir, string name, out bool needsLfn)
    {
        if (TryMakeCleanShort(name, out string clean))
        {
            needsLfn = false;
            return clean; // 11-char padded
        }
        needsLfn = true;

        // collect existing raw 8.3 name fields to guarantee alias uniqueness
        var existingShort = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        var rec = new byte[FatDirEntry.RecordSize];
        while (ReadRawRecord(dir, idx, rec))
        {
            if (rec[0] == 0x00) break;
            if (rec[0] != 0xE5 && ((FatAttr)rec[11] & FatAttr.LongName) != FatAttr.LongName)
                existingShort.Add(Encoding.ASCII.GetString(rec, 0, 11));
            idx++;
        }

        string baseName = SanitizeForShort(name, out string ext);
        for (int k = 1; k < 100000; k++)
        {
            string tail = "~" + k;
            int keep = Math.Max(1, 8 - tail.Length);
            string shortBase = (baseName.Length > keep ? baseName[..keep] : baseName) + tail;
            string padded = PadShort(shortBase, ext);
            if (!existingShort.Contains(padded))
                return padded;
        }
        throw new IOException("Cannot generate a unique short name");
    }

    // True if the name is already a valid uppercase 8.3 name.
    private static bool TryMakeCleanShort(string name, out string padded)
    {
        padded = "";
        int dot = name.LastIndexOf('.');
        string b = dot >= 0 ? name[..dot] : name;
        string e = dot >= 0 ? name[(dot + 1)..] : "";
        if (b.Length == 0 || b.Length > 8 || e.Length > 3) return false;
        if (name != name.ToUpperInvariant()) return false;
        if (!IsShortClean(b) || !IsShortClean(e)) return false;
        padded = PadShort(b, e);
        return true;
    }

    private static bool IsShortClean(string s)
    {
        foreach (char c in s)
            if (c <= ' ' || "+,;=[].\"*/:<>?\\|".IndexOf(c) >= 0) return false;
        return true;
    }

    private static string SanitizeForShort(string name, out string ext)
    {
        name = name.ToUpperInvariant();
        int dot = name.LastIndexOf('.');
        string b = dot >= 0 ? name[..dot] : name;
        ext = dot >= 0 ? name[(dot + 1)..] : "";
        ext = new string(ext.Where(c => IsShortClean(c.ToString())).Take(3).ToArray());
        b = new string(b.Where(c => c > ' ' && "+,;=[]\"*/:<>?\\|.".IndexOf(c) < 0).ToArray());
        if (b.Length == 0) b = "FILE";
        return b;
    }

    private static string PadShort(string b, string e) =>
        b.ToUpperInvariant().PadRight(8).Substring(0, 8) + e.ToUpperInvariant().PadRight(3).Substring(0, 3);

    private static byte ShortNameChecksum(string padded11)
    {
        byte sum = 0;
        for (int i = 0; i < 11; i++)
            sum = (byte)(((sum & 1) != 0 ? 0x80 : 0) + (sum >> 1) + (byte)padded11[i]);
        return sum;
    }

    private static List<string> SplitLfn(string name)
    {
        // 13 UTF-16 chars per entry; pad with 0x0000 then 0xFFFF.
        var chunks = new List<string>();
        for (int i = 0; i < name.Length; i += 13)
            chunks.Add(name.Substring(i, Math.Min(13, name.Length - i)));
        if (chunks.Count == 0 || name.Length % 13 == 0) chunks.Add("");
        return chunks;
    }

    private static byte[] LfnEntry(string chunk, int seq, bool last, byte checksum)
    {
        var rec = new byte[FatDirEntry.RecordSize];
        rec[0] = (byte)(last ? (seq | 0x40) : seq);
        rec[11] = (byte)FatAttr.LongName;
        rec[13] = checksum;
        // build 13 UTF-16LE chars: name, then 0x0000 terminator, then 0xFFFF pad
        var buf = new byte[26];
        for (int i = 0; i < 13; i++)
        {
            ushort ch;
            if (i < chunk.Length) ch = chunk[i];
            else if (i == chunk.Length) ch = 0x0000;
            else ch = 0xFFFF;
            buf[i * 2] = (byte)(ch & 0xff);
            buf[i * 2 + 1] = (byte)(ch >> 8);
        }
        // scatter into the record layout: 1..10, 14..25, 28..31
        Array.Copy(buf, 0, rec, 1, 10);
        Array.Copy(buf, 10, rec, 14, 12);
        Array.Copy(buf, 22, rec, 28, 4);
        return rec;
    }

    private static byte[] ShortEntryRaw(string padded11, FatAttr attr, long firstCluster, long size)
    {
        var e = new byte[FatDirEntry.RecordSize];
        Encoding.ASCII.GetBytes(padded11).CopyTo(e, 0);
        e[11] = (byte)attr;
        e[20] = (byte)((firstCluster >> 16) & 0xff); // high word (FAT32)
        e[21] = (byte)((firstCluster >> 24) & 0xff);
        e[26] = (byte)(firstCluster & 0xff);
        e[27] = (byte)((firstCluster >> 8) & 0xff);
        e[28] = (byte)(size & 0xff);
        e[29] = (byte)((size >> 8) & 0xff);
        e[30] = (byte)((size >> 16) & 0xff);
        e[31] = (byte)((size >> 24) & 0xff);
        return e;
    }

    private static byte[] ShortEntry(string name, FatAttr attr, long firstCluster, long size)
        => ShortEntryRaw(PadShort(name, ""), attr, firstCluster, size);

    private void ZeroCluster(long cluster)
    {
        var zeros = new byte[_bpb.ClusterSize];
        _io.Seek(_bpb.GetClusterOffset(cluster));
        _io.Write(zeros, 0, zeros.Length);
    }
}
