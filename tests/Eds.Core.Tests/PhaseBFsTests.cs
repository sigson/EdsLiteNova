using System.Text;
using Eds.Core.Fs;
using Eds.Core.Fs.Std;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase B tests: StringPathUtil (pure path logic) and StdFs (device filesystem
/// over the host FS). See porting gap guide §4.1, §4.5.
/// </summary>
public class StringPathUtilTests
{
    [Theory]
    [InlineData("/a/b/c", new[] { "a", "b", "c" })]
    [InlineData("a/b/c", new[] { "a", "b", "c" })]
    [InlineData("//a///b/", new[] { "a", "b" })]
    [InlineData("", new string[0])]
    [InlineData("/", new string[0])]
    public void SplitPath_Works(string input, string[] expected)
    {
        Assert.Equal(expected, StringPathUtil.SplitPath(input).ToArray());
    }

    [Fact]
    public void Join_Parent_Combine()
    {
        var p = new StringPathUtil("/foo/bar/baz.txt");
        Assert.Equal("/foo/bar/baz.txt", p.ToString());
        Assert.Equal("/foo/bar", p.GetParentPath().ToString());
        Assert.Equal("baz.txt", p.GetFileName());
        Assert.Equal("baz", p.GetFileNameWithoutExtension());
        Assert.Equal("txt", p.GetFileExtension());
        Assert.Equal("/foo/bar/baz.txt/child", p.Combine("child").ToString());
    }

    [Fact]
    public void Root_And_Empty()
    {
        Assert.Equal("/", new StringPathUtil("").ToString());
        Assert.True(new StringPathUtil("/").IsEmpty);
        Assert.Equal("", new StringPathUtil("/").GetFileName());
    }

    [Fact]
    public void Equality_IsCaseInsensitive()
    {
        Assert.Equal(new StringPathUtil("/Foo/Bar"), new StringPathUtil("/foo/bar"));
        Assert.Equal(new StringPathUtil("/Foo/Bar").GetHashCode(),
                     new StringPathUtil("/foo/bar").GetHashCode());
        Assert.NotEqual(new StringPathUtil("/foo/bar"), new StringPathUtil("/foo/baz"));
    }

    [Fact]
    public void IsParentDir_And_SubPath()
    {
        var parent = new StringPathUtil("/a/b");
        Assert.True(parent.IsParentDir(new StringPathUtil("/A/B/c/d")));
        Assert.False(parent.IsParentDir(new StringPathUtil("/a/b")));
        Assert.Equal("/c/d", new StringPathUtil("/a/b/c/d").GetSubPath(parent).ToString());
    }
}

public class StdFsTests
{
    private static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"eds_stdfs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Create_Write_Read_List_Rename_Delete()
    {
        string root = NewTempRoot();
        try
        {
            var fs = new StdFs(root);
            var rootDir = fs.GetRootPath().GetDirectory();

            // create subdirectory + file
            var sub = rootDir.CreateDirectory("sub");
            var file = sub.CreateFile("data.bin");

            var payload = Encoding.UTF8.GetBytes("hello std-fs 0123456789 ✔");
            using (var io = file.GetRandomAccessIO(FileAccessMode.ReadWrite))
            {
                io.Seek(0);
                io.Write(payload, 0, payload.Length);
                io.Flush();
            }

            // size + read back
            var reopened = fs.GetPath("/sub/data.bin").GetFile();
            Assert.Equal(payload.Length, reopened.GetSize());
            using (var io = reopened.GetRandomAccessIO(FileAccessMode.Read))
            {
                var read = new byte[payload.Length];
                int total = 0, n;
                while (total < read.Length && (n = io.Read(read, total, read.Length - total)) > 0) total += n;
                Assert.Equal(payload.Length, total);
                Assert.True(read.AsSpan().SequenceEqual(payload));
            }

            // list
            using (var contents = sub.List())
            {
                var names = contents.Select(p => new StringPathUtil(p.PathString).GetFileName()).OrderBy(x => x).ToArray();
                Assert.Contains("data.bin", names);
            }

            // rename
            reopened.Rename("renamed.bin");
            Assert.False(fs.GetPath("/sub/data.bin").Exists());
            Assert.True(fs.GetPath("/sub/renamed.bin").Exists());

            // delete file then dir
            fs.GetPath("/sub/renamed.bin").GetFile().Delete();
            Assert.False(fs.GetPath("/sub/renamed.bin").Exists());
            sub.Delete();
            Assert.False(fs.GetPath("/sub").Exists());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void GetPathUtil_Extension_Helpers()
    {
        string root = NewTempRoot();
        try
        {
            var fs = new StdFs(root);
            var p = fs.GetPath("/dir/file.txt");
            Assert.Equal("file.txt", new StringPathUtil(p.PathString).GetFileName());
            Assert.Equal("/dir", p.GetParentPath()!.PathString);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void RandomAccessStreams_RoundTrip()
    {
        string root = NewTempRoot();
        try
        {
            var fs = new StdFs(root);
            var file = fs.GetRootPath().GetDirectory().CreateFile("s.bin");
            var payload = new byte[5000];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);

            using (var io = file.GetRandomAccessIO(FileAccessMode.ReadWrite))
            using (var os = new RandomAccessOutputStream(io, ownsIo: false))
            {
                os.Write(payload, 0, payload.Length);
                os.Flush();
            }

            using (var io = fs.GetPath("/s.bin").GetFile().GetRandomAccessIO(FileAccessMode.Read))
            using (var ins = new RandomAccessInputStream(io, ownsIo: false))
            {
                var read = new byte[payload.Length];
                int total = 0, n;
                while (total < read.Length && (n = ins.Read(read, total, read.Length - total)) > 0) total += n;
                Assert.Equal(payload.Length, total);
                Assert.True(read.AsSpan().SequenceEqual(payload));
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
