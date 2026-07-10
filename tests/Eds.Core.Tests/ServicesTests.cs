using System.Text;
using Eds.Core.Containers;
using Eds.Core.Containers.Locations;
using Eds.Core.Crypto;
using Eds.Core.Fs.Std;
using Eds.Core.Fs.Vfs;
using Eds.Core.Locations;
using Eds.Core.Services;
using Eds.Core.Settings;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase E: services + file operations. Covers the platform-independent core —
/// recursive copy/move/delete/wipe over the Vfs, the serialized operations queue,
/// idle auto-close of opened locations, and the decrypt→edit→re-encrypt temp-file
/// round-trip.
/// </summary>
public class ServicesTests
{
    // ---- pure file operations over StdFs -------------------------------

    [Fact]
    public void Copy_Tree_Copies_Structure_And_Reports_Progress()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var sfs = new StdFs(src.Path);
        var dfs = new StdFs(dst.Path);

        WriteFile(sfs, "/a.txt", "AAA");
        sfs.GetRootPath().GetDirectory().CreateDirectory("sub");
        WriteFile(sfs, "/sub/b.txt", "BBBB");

        var sources = new List<IPath> { sfs.GetPath("/a.txt"), sfs.GetPath("/sub") };
        FileOperationStatus last = default;
        var progress = new SyncProgress(s => last = s);

        new FileOperations(progress).Copy(sources, dfs.GetRootPath().GetDirectory());

        Assert.Equal("AAA", ReadFile(dfs, "/a.txt"));
        Assert.Equal("BBBB", ReadFile(dfs, "/sub/b.txt"));
        Assert.Equal(2, last.FilesProcessed);
        Assert.Equal(2, last.TotalFiles);
        Assert.Equal(7, last.TotalBytes);
        Assert.Equal(7, last.BytesProcessed);
    }

    [Fact]
    public void Move_SameFs_Relocates_Without_Copy()
    {
        using var root = new TempDir();
        var fs = new StdFs(root.Path);
        WriteFile(fs, "/a.txt", "hello");
        fs.GetRootPath().GetDirectory().CreateDirectory("dst");

        new FileOperations().Move(
            new List<IPath> { fs.GetPath("/a.txt") },
            fs.GetPath("/dst").GetDirectory());

        Assert.False(fs.GetPath("/a.txt").Exists());
        Assert.True(fs.GetPath("/dst/a.txt").Exists());
        Assert.Equal("hello", ReadFile(fs, "/dst/a.txt"));
    }

    [Fact]
    public void Delete_Removes_Directory_Tree()
    {
        using var root = new TempDir();
        var fs = new StdFs(root.Path);
        fs.GetRootPath().GetDirectory().CreateDirectory("sub");
        WriteFile(fs, "/sub/x.txt", "x");

        new FileOperations().Delete(new List<IPath> { fs.GetPath("/sub") });

        Assert.False(fs.GetPath("/sub").Exists());
    }

    [Fact]
    public void Wipe_Overwrites_Then_Deletes_And_Counts_Bytes()
    {
        using var root = new TempDir();
        var fs = new StdFs(root.Path);
        var payload = new byte[1000];
        Random.Shared.NextBytes(payload);
        using (var io = fs.GetPath("/secret.bin").GetFile().GetRandomAccessIO(FileAccessMode.ReadWriteTruncate))
        {
            io.Write(payload, 0, payload.Length);
            io.Flush();
        }

        FileOperationStatus last = default;
        new FileOperations(new SyncProgress(s => last = s)).Wipe(new List<IPath> { fs.GetPath("/secret.bin") });

        Assert.False(fs.GetPath("/secret.bin").Exists());
        Assert.Equal(1000, last.BytesProcessed); // one pass over 1000 bytes
        Assert.Equal(1, last.FilesProcessed);
    }

    // ---- operations service (queue + cancellation) --------------------

    [Fact]
    public async Task Service_CopyAsync_Succeeds()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var sfs = new StdFs(src.Path);
        var dfs = new StdFs(dst.Path);
        WriteFile(sfs, "/f.txt", "data");

        using var svc = new FileOperationsService();
        var res = await svc.CopyAsync(
            new List<IPath> { sfs.GetPath("/f.txt") }, dfs.GetRootPath().GetDirectory());

        Assert.True(res.Success);
        Assert.Equal("data", ReadFile(dfs, "/f.txt"));
    }

    [Fact]
    public async Task Service_Honours_PreCancelledToken()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var sfs = new StdFs(src.Path);
        WriteFile(sfs, "/f.txt", "data");

        using var svc = new FileOperationsService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var res = await svc.CopyAsync(
            new List<IPath> { sfs.GetPath("/f.txt") },
            new StdFs(dst.Path).GetRootPath().GetDirectory(), null, cts.Token);

        Assert.True(res.Cancelled);
        Assert.False(res.Success);
    }

    // ---- auto-close ----------------------------------------------------

    [Fact]
    public void AutoClose_Closes_Idle_But_Not_Fresh_Or_Disabled()
    {
        var settings = new InMemorySettings();
        var password = "pw"u8.ToArray();
        var (dir, file) = NewContainer(password);
        try
        {
            var mgr = NewManager(settings);
            var loc = new ContainerLocation(settings, new DeviceLocation(settings, dir, "/" + Path.GetFileName(file)));
            mgr.AddNewLocation(loc, store: false);
            loc.SetPassword(new SecureBuffer(password));
            loc.Open();
            loc.SetAutoCloseTimeout(60);

            var svc = new AutoCloseService(mgr);
            var lastActivity = loc.GetLastActivityTime();

            // Not yet idle enough → stays open.
            Assert.Equal(0, svc.CloseIdleLocations(lastActivity.AddSeconds(30)));
            Assert.True(loc.IsOpen());

            // Past the timeout → closes.
            Assert.Equal(1, svc.CloseIdleLocations(lastActivity.AddSeconds(61)));
            Assert.False(loc.IsOpen());
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void AutoClose_Disabled_Timeout_Never_Closes()
    {
        var settings = new InMemorySettings();
        var password = "pw"u8.ToArray();
        var (dir, file) = NewContainer(password);
        try
        {
            var mgr = NewManager(settings);
            var loc = new ContainerLocation(settings, new DeviceLocation(settings, dir, "/" + Path.GetFileName(file)));
            mgr.AddNewLocation(loc, store: false);
            loc.SetPassword(new SecureBuffer(password));
            loc.Open();
            loc.SetAutoCloseTimeout(0); // disabled

            var svc = new AutoCloseService(mgr);
            Assert.Equal(0, svc.CloseIdleLocations(loc.GetLastActivityTime().AddDays(365)));
            Assert.True(loc.IsOpen());
        }
        finally { TryDeleteDir(dir); }
    }

    // ---- temp file round-trip -----------------------------------------

    [Fact]
    public async Task TempFile_Decrypt_Edit_Reencrypt_RoundTrip()
    {
        var settings = new InMemorySettings();
        var password = "pw"u8.ToArray();
        var (dir, file) = NewContainer(password);
        using var tempDir = new TempDir();
        try
        {
            var mgr = NewManager(settings);
            var loc = new ContainerLocation(settings, new DeviceLocation(settings, dir, "/" + Path.GetFileName(file)));
            mgr.AddNewLocation(loc, store: false);
            loc.SetPassword(new SecureBuffer(password));
            loc.Open();
            var fs = loc.GetFileSystem();

            // Seed a file inside the container.
            WriteFile(fs, "/doc.txt", "version one");

            var tfm = new TempFileManager(tempDir.Path);
            var handle = tfm.PrepareTempFile(fs.GetPath("/doc.txt").GetFile());

            Assert.False(tfm.HasChanged(handle));
            Assert.Equal("version one", File.ReadAllText(handle.TempPath));

            // Edit the decrypted temp copy on disk, then save back.
            File.WriteAllText(handle.TempPath, "version two — longer");
            Assert.True(tfm.HasChanged(handle));
            Assert.True(tfm.SaveChanges(handle));

            Assert.Equal("version two — longer", ReadFile(fs, "/doc.txt"));
            tfm.Clear(handle);
            Assert.False(File.Exists(handle.TempPath));

            // OpenAndTrack: a fake external opener appends to the temp file.
            var opener = new AppendingOpener(" +edit");
            bool changed = await tfm.OpenAndTrackAsync(fs.GetPath("/doc.txt").GetFile(), opener);
            Assert.True(changed);
            Assert.Equal("version two — longer +edit", ReadFile(fs, "/doc.txt"));

            mgr.CloseLocation(loc, force: false);
        }
        finally { TryDeleteDir(dir); }
    }

    // ---- helpers -------------------------------------------------------

    private static LocationsManager NewManager(ISettings settings)
    {
        var mgr = new LocationsManager(settings).RegisterCoreFactories();
        mgr.RegisterFactory(new ContainerLocationFactory());
        return mgr;
    }

    private static (string dir, string file) NewContainer(byte[] password)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"eds_svc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "vault.hc");
        ContainerCreator.Create(file, password, new ContainerCreator.Options
        {
            Format = ContainerCreator.Format.TrueCrypt,
            Cipher = ContainerCreator.Cipher.Aes,
            Hash = ContainerCreator.Hash.Sha512,
            VolumeSize = 8 * 1024 * 1024,
            FormatFat = true,
        });
        return (dir, file);
    }

    private static void WriteFile(IFileSystem fs, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var f = fs.GetPath(path).GetFile();
        using var io = f.GetRandomAccessIO(FileAccessMode.ReadWriteTruncate);
        io.Seek(0);
        io.Write(bytes, 0, bytes.Length);
        io.Flush();
    }

    private static string ReadFile(IFileSystem fs, string path)
    {
        var f = fs.GetPath(path).GetFile();
        using var io = f.GetRandomAccessIO(FileAccessMode.Read);
        long n = f.GetSize();
        var buf = new byte[n];
        int off = 0;
        io.Seek(0);
        while (off < n)
        {
            int r = io.Read(buf, off, (int)(n - off));
            if (r <= 0) break;
            off += r;
        }
        return Encoding.UTF8.GetString(buf);
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"eds_tmp_{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private sealed class SyncProgress(Action<FileOperationStatus> onReport) : IProgress<FileOperationStatus>
    {
        public void Report(FileOperationStatus value) => onReport(value);
    }

    private sealed class AppendingOpener(string suffix) : IExternalFileOpener
    {
        public Task OpenAsync(string tempFilePath, string? mimeType, CancellationToken ct)
        {
            File.AppendAllText(tempFilePath, suffix);
            return Task.CompletedTask;
        }
    }
}
