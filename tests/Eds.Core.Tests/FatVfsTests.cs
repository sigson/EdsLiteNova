using Eds.Core.Fs;
using Eds.Core.Fs.Fat;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase B tail: FAT usable under the unified Vfs contract (<see cref="FatVfs"/>).
/// Mounts a FAT16 image and exercises create/write/read/list/rename/delete purely
/// through the Vfs interfaces (the same surface StdFs and EncFS implement).
/// </summary>
public class FatVfsTests
{
    private const long Size = 8 * 1024 * 1024; // FAT16

    private static MemoryStream NewFat16()
    {
        var stream = new MemoryStream(new byte[Size]);
        using (var io = new StreamRandomAccessIO(stream, ownsStream: false))
            FatFormatter.FormatFat16(io, Size);
        stream.Position = 0;
        return stream;
    }

    private static string LeafName(IPath p) => new StringPathUtil(p.PathString).GetFileName();

    [Fact]
    public void FatVfs_Create_Write_Read_List_Rename_Delete()
    {
        var stream = NewFat16();
        using var io = new StreamRandomAccessIO(stream, ownsStream: false);
        var fs = new FatVfs(FatFileSystem.Mount(io), writable: true);

        var payload = new byte[5000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 29 + 7);

        // create dir + file, write through a Vfs random-access handle
        var root = fs.GetRootPath().GetDirectory();
        var sub = root.CreateDirectory("MyDir");
        var file = sub.CreateFile("data.bin");
        using (var w = file.GetRandomAccessIO(FileAccessMode.ReadWrite))
        {
            w.Seek(0);
            w.Write(payload, 0, payload.Length);
            w.Flush();
        }

        // read back via a freshly resolved path
        var fp = fs.GetPath("/MyDir/data.bin");
        Assert.True(fp.Exists());
        Assert.True(fp.IsFile());
        var f2 = fp.GetFile();
        Assert.Equal(payload.Length, f2.GetSize());
        using (var r = f2.GetRandomAccessIO(FileAccessMode.Read))
        {
            var read = new byte[payload.Length];
            int total = 0, n;
            while (total < read.Length && (n = r.Read(read, total, read.Length - total)) > 0) total += n;
            Assert.Equal(payload.Length, total);
            Assert.True(read.AsSpan().SequenceEqual(payload));
        }

        // listing exposes the child
        var names = new List<string>();
        using (var contents = sub.List())
            foreach (var p in contents) names.Add(LeafName(p));
        Assert.Contains("data.bin", names);

        // rename then delete
        f2.Rename("renamed.bin");
        Assert.True(fs.GetPath("/MyDir/renamed.bin").Exists());
        Assert.False(fs.GetPath("/MyDir/data.bin").Exists());

        fs.GetPath("/MyDir/renamed.bin").GetFile().Delete();
        Assert.False(fs.GetPath("/MyDir/renamed.bin").Exists());
    }

    [Fact]
    public void FatVfs_ReadOnly_Rejects_Writes()
    {
        var stream = NewFat16();
        using var io = new StreamRandomAccessIO(stream, ownsStream: false);
        var fs = new FatVfs(FatFileSystem.Mount(io), writable: false);

        var root = fs.GetRootPath().GetDirectory();
        Assert.Throws<NotSupportedException>(() => root.CreateDirectory("x"));
        Assert.Throws<NotSupportedException>(() => root.CreateFile("y.bin"));
    }
}
