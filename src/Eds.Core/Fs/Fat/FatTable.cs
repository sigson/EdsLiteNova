namespace Eds.Core.Fs.Fat;

/// <summary>
/// Reads the File Allocation Table to follow cluster chains. Port of the
/// FAT-navigation parts of <c>fs.fat.FatFS</c> (read path). Handles 12/16/32-bit
/// entries and end-of-chain markers.
/// </summary>
public sealed class FatTable
{
    private readonly IRandomAccessIO _io;
    private readonly Bpb _bpb;

    public FatTable(IRandomAccessIO io, Bpb bpb)
    {
        _io = io;
        _bpb = bpb;
    }

    /// <summary>Returns the next cluster in the chain, or -1 at end-of-chain.</summary>
    public long GetNextCluster(long cluster)
    {
        long value = ReadEntry(cluster);
        return IsEndOfChain(value) ? -1 : value;
    }

    public bool IsEndOfChain(long value) => _bpb.Type switch
    {
        FatType.Fat12 => value >= 0xFF8,
        FatType.Fat16 => value >= 0xFFF8,
        _ => value >= 0x0FFFFFF8,
    };

    private long ReadEntry(long cluster)
    {
        switch (_bpb.Type)
        {
            case FatType.Fat12:
            {
                long offset = _bpb.FirstFatOffset + cluster + cluster / 2;
                _io.Seek(offset);
                int word = IoUtil.ReadWordLE(_io);
                return (cluster & 1) != 0 ? (word >> 4) & 0x0FFF : word & 0x0FFF;
            }
            case FatType.Fat16:
            {
                _io.Seek(_bpb.FirstFatOffset + cluster * 2);
                return IoUtil.ReadWordLE(_io);
            }
            default: // FAT32
            {
                _io.Seek(_bpb.FirstFatOffset + cluster * 4);
                return IoUtil.ReadDoubleWordLE(_io) & 0x0FFFFFFFL;
            }
        }
    }

    /// <summary>Public read of a raw FAT entry value (no EOC translation).</summary>
    public long GetEntry(long cluster) => ReadEntry(cluster);

    /// <summary>End-of-chain marker value for the current FAT type.</summary>
    public long EocMarker => _bpb.Type switch
    {
        FatType.Fat12 => 0xFFF,
        FatType.Fat16 => 0xFFFF,
        _ => 0x0FFFFFFF,
    };

    /// <summary>Writes a FAT entry to every FAT copy on disk.</summary>
    public void SetEntry(long cluster, long value)
    {
        for (int f = 0; f < _bpb.NumberOfFats; f++)
        {
            long fatBase = _bpb.FirstFatOffset + (long)f * _bpb.SectorsPerFat * _bpb.BytesPerSector;
            switch (_bpb.Type)
            {
                case FatType.Fat12:
                {
                    long offset = fatBase + cluster + cluster / 2;
                    _io.Seek(offset);
                    int word = IoUtil.ReadWordLE(_io);
                    if ((cluster & 1) != 0)
                        word = (word & 0x000F) | (int)((value & 0x0FFF) << 4);
                    else
                        word = (word & 0xF000) | (int)(value & 0x0FFF);
                    _io.Seek(offset);
                    IoUtil.WriteWordLE(_io, word);
                    break;
                }
                case FatType.Fat16:
                    _io.Seek(fatBase + cluster * 2);
                    IoUtil.WriteWordLE(_io, (int)(value & 0xFFFF));
                    break;
                default: // FAT32 (preserve top 4 reserved bits)
                {
                    _io.Seek(fatBase + cluster * 4);
                    long cur = IoUtil.ReadDoubleWordLE(_io);
                    long merged = (cur & 0xF0000000L) | (value & 0x0FFFFFFFL);
                    _io.Seek(fatBase + cluster * 4);
                    IoUtil.WriteDoubleWordLE(_io, merged);
                    break;
                }
            }
        }
    }

    /// <summary>Finds and reserves a free cluster (marks it EOC). Returns -1 if full.</summary>
    public long AllocateCluster(long totalClusters)
    {
        for (long c = 2; c < totalClusters + 2; c++)
        {
            if (ReadEntry(c) == 0)
            {
                SetEntry(c, EocMarker);
                return c;
            }
        }
        return -1;
    }

    /// <summary>Enumerates the cluster chain starting at <paramref name="firstCluster"/>.</summary>
    public IEnumerable<long> Chain(long firstCluster)
    {
        long c = firstCluster;
        var seen = new HashSet<long>();
        while (c >= 2 && !IsEndOfChain(c))
        {
            if (!seen.Add(c)) yield break; // guard against corrupt loops
            yield return c;
            c = ReadEntry(c);
        }
    }
}
