using System.Text;

namespace Eds.Core.Fs.Fat;

[Flags]
public enum FatAttr : byte
{
    ReadOnly = 0x01,
    Hidden = 0x02,
    System = 0x04,
    VolumeLabel = 0x08,
    Directory = 0x10,
    Archive = 0x20,
    LongName = 0x0F,
}

/// <summary>
/// A parsed FAT directory entry. Port of <c>fs.fat.DirEntry</c> (read path),
/// including long-file-name (VFAT) reassembly across preceding 0x0F records.
/// </summary>
public sealed class FatDirEntry
{
    public const int RecordSize = 32;

    public required string Name { get; init; }
    public required FatAttr Attributes { get; init; }
    public required long FirstCluster { get; init; }
    public required long Size { get; init; }

    public bool IsDirectory => (Attributes & FatAttr.Directory) != 0;
    public bool IsVolumeLabel => (Attributes & FatAttr.VolumeLabel) != 0;

    /// <summary>
    /// Parses one directory's raw bytes (a concatenation of 32-byte records) into
    /// entries, reassembling LFNs. Stops at the first 0x00 terminator record.
    /// </summary>
    public static IEnumerable<FatDirEntry> ParseDirectory(byte[] data)
    {
        var lfnParts = new SortedDictionary<int, string>();
        for (int off = 0; off + RecordSize <= data.Length; off += RecordSize)
        {
            byte first = data[off];
            if (first == 0x00) yield break;      // end of directory
            if (first == 0xE5) { lfnParts.Clear(); continue; } // deleted

            var attr = (FatAttr)data[off + 11];
            if ((attr & FatAttr.LongName) == FatAttr.LongName)
            {
                int seq = data[off] & 0x1F;      // 1-based order
                lfnParts[seq] = ExtractLfnChars(data, off);
                continue;
            }

            string name;
            if (lfnParts.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var part in lfnParts.Values) sb.Append(part);
                name = TrimLfn(sb.ToString());
                lfnParts.Clear();
            }
            else
            {
                name = ParseShortName(data, off);
            }

            long hi = IoUtil.ReadWordLE(data, off + 20);
            long lo = IoUtil.ReadWordLE(data, off + 26);
            long firstCluster = (hi << 16) | lo;
            long size = IoUtil.ReadDoubleWordLE(data, off + 28);

            if ((attr & FatAttr.VolumeLabel) != 0 && (attr & FatAttr.Directory) == 0)
                continue; // skip the volume label entry

            yield return new FatDirEntry
            {
                Name = name,
                Attributes = attr,
                FirstCluster = firstCluster,
                Size = size,
            };
        }
    }

    private static string ExtractLfnChars(byte[] data, int off)
    {
        // positions of the 13 UTF-16LE chars within the 32-byte LFN record:
        // bytes 1..10 (5 chars), 14..25 (6 chars), 28..31 (2 chars)
        var chars = new byte[26];
        int idx = 0;
        for (int i = 0; i < 10; i++) chars[idx++] = data[off + 1 + i];
        for (int i = 0; i < 12; i++) chars[idx++] = data[off + 14 + i];
        for (int i = 0; i < 4; i++) chars[idx++] = data[off + 28 + i];
        return Encoding.Unicode.GetString(chars);
    }

    private static string TrimLfn(string s)
    {
        int end = s.IndexOf('\uFFFF');
        if (end >= 0) s = s[..end];
        int nul = s.IndexOf('\0');
        if (nul >= 0) s = s[..nul];
        return s;
    }

    private static string ParseShortName(byte[] data, int off)
    {
        var name = new byte[8];
        var ext = new byte[3];
        Array.Copy(data, off, name, 0, 8);
        Array.Copy(data, off + 8, ext, 0, 3);
        if (name[0] == 0x05) name[0] = 0xE5;

        string n = Encoding.ASCII.GetString(name).TrimEnd(' ');
        string e = Encoding.ASCII.GetString(ext).TrimEnd(' ');
        return e.Length > 0 ? $"{n}.{e}" : n;
    }
}
