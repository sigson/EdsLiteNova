using System.Text;
using Eds.Core.Containers;
using Eds.Core.Crypto;
using Eds.Core.Fs;
using Eds.Core.Fs.Fat;
using Xunit;

namespace Eds.Core.Tests;

public class FatReadTests
{
    // Builds a minimal but valid FAT12 image with a single root-directory file.
    // Layout (512-byte sectors): [0]=boot, [1]=FAT, [2]=root dir, [3]=cluster 2 (data).
    private static byte[] BuildFat12Image(string fileName83, byte[] content)
    {
        const int sectorSize = 512;
        const int totalSectors = 20;
        var img = new byte[totalSectors * sectorSize];

        // ---- boot sector BPB ----
        void W16(int off, int v) { img[off] = (byte)(v & 0xff); img[off + 1] = (byte)((v >> 8) & 0xff); }
        img[11] = 0x00; img[12] = 0x02;   // bytesPerSector = 512
        img[13] = 1;                       // sectorsPerCluster
        W16(14, 1);                        // reservedSectors
        img[16] = 1;                       // numberOfFATs
        W16(17, 16);                       // rootDirEntries
        W16(19, totalSectors);             // totalSectors16
        img[21] = 0xF8;                    // media type
        W16(22, 1);                        // sectorsPerFat16
        W16(24, 1);                        // sectorsPerTrack
        W16(26, 1);                        // numberOfHeads
        img[510] = 0x55; img[511] = 0xAA;  // signature

        // ---- FAT (sector 1, offset 512): cluster 2 -> EOC ----
        int fat = 512;
        img[fat + 0] = 0xF8; img[fat + 1] = 0xFF; img[fat + 2] = 0xFF; // entries 0,1
        img[fat + 3] = 0xFF; img[fat + 4] = 0xFF;                      // cluster 2 = 0xFFF (EOC)

        // ---- root dir (sector 2, offset 1024): one 8.3 entry ----
        int dir = 1024;
        var nameField = Encoding.ASCII.GetBytes(fileName83); // exactly 11 chars "HELLO   TXT"
        Array.Copy(nameField, 0, img, dir, 11);
        img[dir + 11] = 0x20;              // archive
        img[dir + 26] = 2; img[dir + 27] = 0; // first cluster low = 2
        int size = content.Length;
        img[dir + 28] = (byte)(size & 0xff);
        img[dir + 29] = (byte)((size >> 8) & 0xff);
        img[dir + 30] = (byte)((size >> 16) & 0xff);
        img[dir + 31] = (byte)((size >> 24) & 0xff);

        // ---- data (cluster 2, offset 1536) ----
        Array.Copy(content, 0, img, 1536, content.Length);
        return img;
    }

    [Fact]
    public void Fat12_Mount_List_Read()
    {
        var content = "Hello, FAT!\n"u8.ToArray();
        var img = BuildFat12Image("HELLO   TXT", content);

        using var io = new StreamRandomAccessIO(new MemoryStream(img), ownsStream: true);
        var fs = FatFileSystem.Mount(io);
        Assert.Equal(FatType.Fat12, fs.Type);

        var root = fs.ListRoot();
        var file = Assert.Single(root);
        Assert.Equal("HELLO.TXT", file.Name);
        Assert.False(file.IsDirectory);
        Assert.Equal(content.Length, file.Size);

        var read = fs.ReadAllBytes(file);
        Assert.Equal(content, read);

        // path resolution is case-insensitive
        var resolved = fs.ResolvePath("/hello.txt");
        Assert.NotNull(resolved);
        Assert.Equal("HELLO.TXT", resolved!.Name);
    }

    [Fact]
    public void Fat16_Format_Write_Remount_Read()
    {
        const long size = 8 * 1024 * 1024; // 8 MiB -> FAT16
        var stream = new MemoryStream(new byte[size]);
        var small = "small file"u8.ToArray();
        var big = new byte[10000];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 31 + 7);

        using (var io = new StreamRandomAccessIO(stream, ownsStream: false))
        {
            FatFormatter.FormatFat16(io, size);
            var fs = FatFileSystem.Mount(io);
            Assert.Equal(FatType.Fat16, fs.Type);
            fs.WriteFileToRoot("small.txt", small);
            fs.WriteFileToRoot("big.bin", big); // spans multiple clusters
        }

        stream.Position = 0;
        using (var io = new StreamRandomAccessIO(stream, ownsStream: false))
        {
            var fs = FatFileSystem.Mount(io);
            var names = fs.ListRoot().Select(e => e.Name).OrderBy(n => n).ToList();
            Assert.Contains("SMALL.TXT", names);
            Assert.Contains("BIG.BIN", names);

            Assert.Equal(small, fs.ReadAllBytes(fs.ResolvePath("/SMALL.TXT")!));
            Assert.Equal(big, fs.ReadAllBytes(fs.ResolvePath("/BIG.BIN")!));
        }
    }

    [Fact]
    public void FullStack_Container_Format_Fat_Write_Reopen_Read()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"eds_fs_{Guid.NewGuid():N}.tc");
        const long volumeBytes = 8 * 1024 * 1024;
        // container total must exceed reserved header + volume; give headroom
        const long containerSize = volumeBytes + 512 * 1024;
        var password = "container+fat"u8.ToArray();
        var payload = "end-to-end: container -> FAT -> file"u8.ToArray();

        try
        {
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, true))
            {
                var layout = new StdLayout();
                layout.SetPassword((byte[])password.Clone());
                layout.FormatNew(baseIo, containerSize);
                using var vol = new EncryptedFile(baseIo, layout);
                FatFormatter.FormatFat16(vol, volumeBytes);
                var fs = FatFileSystem.Mount(vol);
                fs.WriteFileToRoot("readme.txt", payload);
                vol.Flush();
                layout.Dispose();
            }

            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, false))
            using (var container = new EdsContainer(baseIo))
            {
                Assert.True(container.Open(password));
                using var vol = container.GetEncryptedVolume();
                var fs = FatFileSystem.Mount(vol);
                var entry = fs.ResolvePath("/README.TXT");
                Assert.NotNull(entry);
                Assert.Equal(payload, fs.ReadAllBytes(entry!));
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
