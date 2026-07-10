using Eds.Core.App;
using Eds.Core.Fs.Std;
using Eds.Core.Fs.Vfs;
using Eds.Core.Services;
using Eds.Core.Settings;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Directory listing sort/filter for the file manager (Phase H prep).
/// A fixed tree: dirs "alpha","zebra"; files "a.log"(10), "b.txt"(3), "c.txt"(1).
/// </summary>
public class FileListerTests
{
    [Fact]
    public void Sort_By_Name_Ascending_Dirs_First()
    {
        using var t = Tree();
        var names = Names(new FileLister().List(t.Root, new FileListOptions
        {
            Field = FileSortField.Name,
            Direction = SortDirection.Ascending,
            DirectoriesFirst = true,
        }));
        Assert.Equal(new[] { "alpha", "zebra", "a.log", "b.txt", "c.txt" }, names);
    }

    [Fact]
    public void Sort_By_Name_Descending_Keeps_Dirs_First()
    {
        using var t = Tree();
        var names = Names(new FileLister().List(t.Root, new FileListOptions
        {
            Field = FileSortField.Name,
            Direction = SortDirection.Descending,
            DirectoriesFirst = true,
        }));
        // Directories stay grouped first even when descending.
        Assert.Equal(new[] { "zebra", "alpha", "c.txt", "b.txt", "a.log" }, names);
    }

    [Fact]
    public void Sort_By_Size_Ascending()
    {
        using var t = Tree();
        var names = Names(new FileLister().List(t.Root, new FileListOptions
        {
            Field = FileSortField.Size,
            DirectoriesFirst = true,
        }));
        // Dirs first (name tie-break), then files by size: c(1) < b(3) < a.log(10).
        Assert.Equal(new[] { "alpha", "zebra", "c.txt", "b.txt", "a.log" }, names);
    }

    [Fact]
    public void Sort_By_Type_Groups_By_Extension()
    {
        using var t = Tree();
        var names = Names(new FileLister().List(t.Root, new FileListOptions
        {
            Field = FileSortField.Type,
            DirectoriesFirst = true,
        }));
        // Dirs first, then "log" before "txt", txt files by name.
        Assert.Equal(new[] { "alpha", "zebra", "a.log", "b.txt", "c.txt" }, names);
    }

    [Fact]
    public void DirectoriesFirst_False_Mixes_By_Name()
    {
        using var t = Tree();
        var names = Names(new FileLister().List(t.Root, new FileListOptions
        {
            Field = FileSortField.Name,
            DirectoriesFirst = false,
        }));
        Assert.Equal(new[] { "a.log", "alpha", "b.txt", "c.txt", "zebra" }, names);
    }

    [Fact]
    public void Filter_Drops_NonMatching_Entries()
    {
        using var t = Tree();
        var items = new FileLister().List(t.Root, new FileListOptions
        {
            Filter = it => !it.IsDirectory && it.Name.EndsWith(".txt"),
        });
        Assert.Equal(new[] { "b.txt", "c.txt" }, Names(items));
        Assert.All(items, it => Assert.False(it.IsDirectory));
    }

    [Fact]
    public void Controller_Browse_Returns_Sorted_Items()
    {
        using var t = Tree();
        var app = new EdsAppController(new InMemorySettings());
        var items = app.Browse(t.Fs.GetRootPath()); // defaults: name asc, dirs first
        Assert.Equal(new[] { "alpha", "zebra", "a.log", "b.txt", "c.txt" }, Names(items));
    }

    // ---- fixture -------------------------------------------------------

    private static string[] Names(IReadOnlyList<FileListItem> items)
        => items.Select(i => i.Name).ToArray();

    private static Fixture Tree()
    {
        var f = new Fixture();
        var root = f.Fs.GetRootPath().GetDirectory();
        root.CreateDirectory("alpha");
        root.CreateDirectory("zebra");
        WriteN(f.Fs, "/a.log", 10);
        WriteN(f.Fs, "/b.txt", 3);
        WriteN(f.Fs, "/c.txt", 1);
        return f;
    }

    private static void WriteN(IFileSystem fs, string path, int n)
    {
        var buf = new byte[n];
        using var io = fs.GetPath(path).GetFile().GetRandomAccessIO(FileAccessMode.ReadWriteTruncate);
        io.Seek(0);
        io.Write(buf, 0, n);
        io.Flush();
    }

    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"eds_list_{Guid.NewGuid():N}");
        public StdFs Fs { get; }
        public IDirectory Root => Fs.GetRootPath().GetDirectory();

        public Fixture()
        {
            Directory.CreateDirectory(Dir);
            Fs = new StdFs(Dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch { /* ignore */ }
        }
    }
}
