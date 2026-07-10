using System.Security.Cryptography;
using System.Text;

namespace Eds.Core.Fs.Fat;

/// <summary>
/// Creates a fresh FAT16 filesystem in a writable <see cref="IRandomAccessIO"/>
/// (typically the decrypted volume of a container). Complements the FAT reader
/// so a container can be formatted and populated entirely in managed code.
///
/// FAT16 is chosen for the formatter because its geometry is simple and covers
/// the useful volume range (~4 MiB … ~2 GiB); the reader still handles 12/16/32.
/// </summary>
public static class FatFormatter
{
    private const int BytesPerSector = 512;
    private const int ReservedSectors = 1;
    private const int NumberOfFats = 2;
    private const int RootDirEntries = 512;

    public static void FormatFat16(IRandomAccessIO io, long volumeBytes)
    {
        long totalSectors = volumeBytes / BytesPerSector;
        if (totalSectors < 8192)
            throw new ArgumentException("Volume too small for FAT16 (need >= 4 MiB)");

        // Pick sectors-per-cluster so the cluster count stays in FAT16 range.
        int spc = 1;
        while (totalSectors / spc > 60000) spc *= 2;

        int rootDirSectors = (RootDirEntries * 32 + BytesPerSector - 1) / BytesPerSector;
        // Microsoft FAT spec sectors-per-FAT approximation for FAT16.
        long tmp1 = totalSectors - (ReservedSectors + rootDirSectors);
        long tmp2 = 256L * spc + NumberOfFats;
        int sectorsPerFat = (int)((tmp1 + (tmp2 - 1)) / tmp2);

        var boot = new byte[BytesPerSector];
        void W16(int off, int v) { boot[off] = (byte)(v & 0xff); boot[off + 1] = (byte)((v >> 8) & 0xff); }
        void W32(int off, long v)
        {
            boot[off] = (byte)(v & 0xff); boot[off + 1] = (byte)((v >> 8) & 0xff);
            boot[off + 2] = (byte)((v >> 16) & 0xff); boot[off + 3] = (byte)((v >> 24) & 0xff);
        }

        boot[0] = 0xEB; boot[1] = 0x3C; boot[2] = 0x90;            // jmp
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(boot, 3);        // OEM
        W16(11, BytesPerSector);
        boot[13] = (byte)spc;
        W16(14, ReservedSectors);
        boot[16] = NumberOfFats;
        W16(17, RootDirEntries);
        if (totalSectors < 0x10000) W16(19, (int)totalSectors); else W16(19, 0);
        boot[21] = 0xF8;                                           // media
        W16(22, sectorsPerFat);
        W16(24, 63);                                              // sectors/track
        W16(26, 255);                                             // heads
        W32(28, 0);                                               // hidden
        W32(32, totalSectors >= 0x10000 ? totalSectors : 0);      // total32

        // FAT16 extended BPB
        boot[36] = 0x80;                                          // drive number
        boot[38] = 0x29;                                          // ext boot signature
        var serial = new byte[4]; RandomNumberGenerator.Fill(serial); serial.CopyTo(boot, 39);
        Encoding.ASCII.GetBytes("NO NAME    ").CopyTo(boot, 43);   // 11-byte label
        Encoding.ASCII.GetBytes("FAT16   ").CopyTo(boot, 54);      // 8-byte fs type
        boot[510] = 0x55; boot[511] = 0xAA;

        io.Seek(0);
        io.Write(boot, 0, boot.Length);

        // Zero the FATs and root directory, then seed FAT[0]/FAT[1].
        long fatStart = (long)ReservedSectors * BytesPerSector;
        int fatBytes = sectorsPerFat * BytesPerSector;
        var zeros = new byte[BytesPerSector];
        for (int f = 0; f < NumberOfFats; f++)
        {
            io.Seek(fatStart + (long)f * fatBytes);
            for (int s = 0; s < sectorsPerFat; s++) io.Write(zeros, 0, BytesPerSector);
            // FAT[0] = 0xFFF8 (media), FAT[1] = 0xFFFF (EOC)
            io.Seek(fatStart + (long)f * fatBytes);
            io.WriteByte(0xF8); io.WriteByte(0xFF); io.WriteByte(0xFF); io.WriteByte(0xFF);
        }

        long rootStart = fatStart + (long)NumberOfFats * fatBytes;
        io.Seek(rootStart);
        for (int s = 0; s < rootDirSectors; s++) io.Write(zeros, 0, BytesPerSector);

        io.Flush();
    }
}
