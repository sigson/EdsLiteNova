using System.Text;
using Eds.Core.Fs;
using Eds.Core.Fs.Fat;
using Xunit;

namespace Eds.Core.Tests;

public class FatWriteTests
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

    [Fact]
    public void LongFileName_Write_Read()
    {
        var stream = NewFat16();
        var content = "long name payload"u8.ToArray();
        const string longName = "My Long Document (v2).txt";

        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            fs.WriteFile("/" + longName, content);
        }

        stream.Position = 0;
        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            var names = fs.ListRoot().Select(e => e.Name).ToList();
            Assert.Contains(longName, names);

            var entry = fs.ResolvePath("/" + longName);
            Assert.NotNull(entry);
            Assert.Equal(content, fs.ReadAllBytes(entry!));
        }
    }

    [Fact]
    public void Subdirectory_Create_WriteFile_Read()
    {
        var stream = NewFat16();
        var content = "nested"u8.ToArray();

        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            fs.CreateDirectory("/docs");
            fs.WriteFile("/docs/readme.txt", content);
        }

        stream.Position = 0;
        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            var docs = fs.ResolvePath("/docs");
            Assert.NotNull(docs);
            Assert.True(docs!.IsDirectory);

            var entry = fs.ResolvePath("/docs/readme.txt");
            Assert.NotNull(entry);
            Assert.Equal(content, fs.ReadAllBytes(entry!));
        }
    }

    [Fact]
    public void Delete_File_Reclaims_And_Removes()
    {
        var stream = NewFat16();

        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            fs.WriteFile("/temp data.bin", new byte[5000]);
            fs.Delete("/temp data.bin");
        }

        stream.Position = 0;
        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            Assert.Null(fs.ResolvePath("/temp data.bin"));
            // directory should have no visible entries
            Assert.Empty(fs.ListRoot());

            // and we can write again (space reclaimed)
            fs.WriteFile("/again.txt", "ok"u8.ToArray());
            Assert.NotNull(fs.ResolvePath("/again.txt"));
        }
    }

    [Fact]
    public void MultipleFiles_And_Nested_Roundtrip()
    {
        var stream = NewFat16();
        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            fs.CreateDirectory("/a");
            fs.CreateDirectory("/a/b");
            fs.WriteFile("/a/b/deep file.txt", "deep"u8.ToArray());
            fs.WriteFile("/root file.txt", "root"u8.ToArray());
        }
        stream.Position = 0;
        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            Assert.Equal("deep"u8.ToArray(), fs.ReadAllBytes(fs.ResolvePath("/a/b/deep file.txt")!));
            Assert.Equal("root"u8.ToArray(), fs.ReadAllBytes(fs.ResolvePath("/root file.txt")!));
        }
    }

    [Fact]
    public void Adapter_Write_List_Delete_ThroughAbstraction()
    {
        var stream = NewFat16();
        using var io = new StreamRandomAccessIO(stream, false);
        var fs = FatFileSystem.Mount(io);
        var afs = new FatFileSystemAdapter(fs, writable: true);

        Assert.True(afs.IsWritable);
        var root = afs.Root;
        root.CreateSubdirectory("Photos");
        root.WriteFile("notes.txt", "hello abstraction"u8.ToArray());

        var names = root.List().Select(o => o.Name).ToList();
        Assert.Contains("Photos", names);
        Assert.Contains("notes.txt", names);

        var file = (Eds.Core.Fs.Abstract.IFile)root.List().First(o => o.Name == "notes.txt");
        Assert.Equal("hello abstraction"u8.ToArray(), file.ReadAllBytes());

        file.Delete();
        Assert.DoesNotContain("notes.txt", afs.Root.List().Select(o => o.Name));
    }

    [Fact]
    public void Rename_And_Move()
    {
        var stream = NewFat16();
        var content = "movable"u8.ToArray();
        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            fs.CreateDirectory("/dest");
            fs.WriteFile("/old name.txt", content);

            // rename within root
            fs.Rename("/old name.txt", "renamed.txt");
            Assert.Null(fs.ResolvePath("/old name.txt"));
            Assert.NotNull(fs.ResolvePath("/renamed.txt"));
            Assert.Equal(content, fs.ReadAllBytes(fs.ResolvePath("/renamed.txt")!));

            // move into subdirectory
            fs.Move("/renamed.txt", "/dest/renamed.txt");
            Assert.Null(fs.ResolvePath("/renamed.txt"));
            var moved = fs.ResolvePath("/dest/renamed.txt");
            Assert.NotNull(moved);
            Assert.Equal(content, fs.ReadAllBytes(moved!));
        }

        stream.Position = 0;
        using (var io = new StreamRandomAccessIO(stream, false))
        {
            var fs = FatFileSystem.Mount(io);
            Assert.Equal(content, fs.ReadAllBytes(fs.ResolvePath("/dest/renamed.txt")!));
        }
    }

    [Fact]
    public void Adapter_Rename_ThroughAbstraction()
    {
        var stream = NewFat16();
        using var io = new StreamRandomAccessIO(stream, false);
        var fs = FatFileSystem.Mount(io);
        var afs = new FatFileSystemAdapter(fs, writable: true);
        afs.Root.WriteFile("doc.txt", "x"u8.ToArray());
        var file = afs.Root.List().First(o => o.Name == "doc.txt");
        file.Rename("report.txt");
        var names = afs.Root.List().Select(o => o.Name).ToList();
        Assert.Contains("report.txt", names);
        Assert.DoesNotContain("doc.txt", names);
    }
}
