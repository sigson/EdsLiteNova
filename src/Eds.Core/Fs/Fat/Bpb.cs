using Eds.Core.Exceptions;

namespace Eds.Core.Fs.Fat;

public enum FatType { Fat12, Fat16, Fat32 }

/// <summary>
/// FAT BIOS Parameter Block. Port of <c>fs.fat.BPB</c> (base + FAT16 + FAT32),
/// read path only. FAT type is inferred from the cluster count per the FAT
/// specification (&lt;4085 = FAT12, &lt;65525 = FAT16, else FAT32).
/// </summary>
public sealed class Bpb
{
    public int BytesPerSector;
    public int SectorsPerCluster;
    public int ReservedSectors;
    public int NumberOfFats;
    public int RootDirEntries;
    public int TotalSectors16;
    public int MediaType;
    public int SectorsPerFat16;
    public long HiddenSectors;
    public long TotalSectors32;

    // FAT32-only
    public long SectorsPerFat32;
    public int RootClusterNumber = 2;

    public FatType Type { get; private set; }
    public long ClusterOffsetStart { get; private set; }
    public long FirstFatOffset { get; private set; }
    public long RootDirOffset { get; private set; }
    public long CountOfClusters { get; private set; }

    public int SectorsPerFat => Type == FatType.Fat32 ? (int)SectorsPerFat32 : SectorsPerFat16;
    public long TotalSectors => TotalSectors16 == 0 ? TotalSectors32 : TotalSectors16;
    public int ClusterSize => BytesPerSector * SectorsPerCluster;

    public long GetClusterOffset(long cluster) =>
        ClusterOffsetStart + (cluster - 2) * SectorsPerCluster * BytesPerSector;

    public static Bpb Read(IRandomAccessIO io)
    {
        var bpb = new Bpb();
        io.Seek(0xB);
        bpb.BytesPerSector = IoUtil.ReadWordLE(io);
        bpb.SectorsPerCluster = IoUtil.ReadUnsignedByte(io);
        bpb.ReservedSectors = IoUtil.ReadWordLE(io);
        bpb.NumberOfFats = IoUtil.ReadUnsignedByte(io);
        bpb.RootDirEntries = IoUtil.ReadWordLE(io);
        bpb.TotalSectors16 = IoUtil.ReadWordLE(io);
        bpb.MediaType = IoUtil.ReadUnsignedByte(io);
        bpb.SectorsPerFat16 = IoUtil.ReadWordLE(io);
        IoUtil.ReadWordLE(io); // sectors per track
        IoUtil.ReadWordLE(io); // number of heads
        bpb.HiddenSectors = IoUtil.ReadDoubleWordLE(io);
        bpb.TotalSectors32 = IoUtil.ReadDoubleWordLE(io);

        if (bpb.BytesPerSector == 0 || bpb.SectorsPerCluster == 0)
            throw new WrongFileFormatException();

        // FAT32 has SectorsPerFat16 == 0; read the extended fields.
        if (bpb.SectorsPerFat16 == 0)
        {
            io.Seek(0x24);
            bpb.SectorsPerFat32 = IoUtil.ReadDoubleWordLE(io);
            IoUtil.ReadWordLE(io);  // update mode
            IoUtil.ReadWordLE(io);  // version
            bpb.RootClusterNumber = (int)IoUtil.ReadDoubleWordLE(io);
        }

        // Compute type from cluster count (spec-accurate).
        int rootDirSectors =
            (bpb.RootDirEntries * 32 + bpb.BytesPerSector - 1) / bpb.BytesPerSector;
        long dataSectors = bpb.TotalSectors -
            (bpb.ReservedSectors + bpb.NumberOfFats * (long)bpb.SectorsPerFat16Or32() + rootDirSectors);
        long countOfClusters = bpb.SectorsPerCluster == 0 ? 0 : dataSectors / bpb.SectorsPerCluster;

        bpb.Type = countOfClusters < 4085 ? FatType.Fat12
                 : countOfClusters < 65525 ? FatType.Fat16
                 : FatType.Fat32;
        bpb.CountOfClusters = countOfClusters;

        bpb.FirstFatOffset = (long)bpb.ReservedSectors * bpb.BytesPerSector;
        if (bpb.Type == FatType.Fat32)
        {
            bpb.ClusterOffsetStart =
                (long)bpb.BytesPerSector * (bpb.ReservedSectors + bpb.SectorsPerFat32 * bpb.NumberOfFats);
            bpb.RootDirOffset = bpb.GetClusterOffset(bpb.RootClusterNumber);
        }
        else
        {
            bpb.ClusterOffsetStart =
                (long)bpb.BytesPerSector * (bpb.ReservedSectors + bpb.SectorsPerFat16 * bpb.NumberOfFats)
                + bpb.RootDirEntries * 32;
            bpb.RootDirOffset =
                (long)bpb.BytesPerSector * (bpb.ReservedSectors + bpb.SectorsPerFat16 * bpb.NumberOfFats);
        }

        ValidateSignature(io);
        return bpb;
    }

    private int SectorsPerFat16Or32() => SectorsPerFat16 != 0 ? SectorsPerFat16 : (int)SectorsPerFat32;

    private static void ValidateSignature(IRandomAccessIO io)
    {
        io.Seek(0x1FE);
        if (IoUtil.ReadWordLE(io) != 0xAA55)
            throw new WrongFileFormatException();
    }
}
