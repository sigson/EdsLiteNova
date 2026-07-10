using System.Text;
using Eds.Core.App;
using Eds.Core.Containers;
using Eds.Core.Crypto;
using Eds.Core.Exceptions;
using Eds.Core.Fs.EncFs;
using Eds.Core.Fs.Std;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;
using Eds.Core.Locations;
using Eds.Core.Settings;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// The application facade (Phase F prep): proves the whole user flow works through
/// one small API the UI will bind to — register, open by password (off-thread),
/// browse, file-ops, persist across restart, close.
/// </summary>
public class AppControllerTests
{
    private const int Pim = 5; // small PIM => fast VeraCrypt opens

    [Fact]
    public async Task Container_Open_Browse_Write_Close_Through_Controller()
    {
        var settings = new InMemorySettings();
        var app = new EdsAppController(settings);
        var pw = "pw"u8.ToArray();
        var (dir, file) = NewVeraCryptContainer(pw);
        try
        {
            var loc = app.AddContainerLocation(file);
            await app.OpenAsync(loc, new SecureBuffer(pw), pim: Pim);
            Assert.True(loc.IsOpen());

            var fs = app.GetFileSystem(loc);
            WriteFile(fs, "/hello.txt", "via controller");

            var listing = app.List(fs.GetRootPath());
            Assert.Contains(listing, p => new StringPathUtil(p.PathString).GetFileName() == "hello.txt");
            Assert.Equal("via controller", ReadFile(fs, "/hello.txt"));

            app.Close(loc);
            Assert.False(loc.IsOpen());
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public async Task Wrong_Password_Faults_OpenAsync()
    {
        var settings = new InMemorySettings();
        var app = new EdsAppController(settings);
        var (dir, file) = NewVeraCryptContainer("right"u8.ToArray());
        try
        {
            var loc = app.AddContainerLocation(file);
            await Assert.ThrowsAsync<WrongPasswordException>(
                () => app.OpenAsync(loc, new SecureBuffer("wrong"u8.ToArray()), pim: Pim));
            Assert.False(loc.IsOpen());
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public async Task Location_Persists_Across_Controller_Restart()
    {
        var settings = new InMemorySettings();
        var pw = "pw"u8.ToArray();
        var (dir, file) = NewVeraCryptContainer(pw);
        try
        {
            var app1 = new EdsAppController(settings);
            app1.AddContainerLocation(file, store: true);

            // "Restart": a fresh controller over the same settings.
            var app2 = new EdsAppController(settings);
            app2.LoadStoredLocations();
            var loc = app2.GetLocations(onlyVisible: false).OfType<IEdsLocation>().Single();

            await app2.OpenAsync(loc, new SecureBuffer(pw), pim: Pim);
            Assert.True(loc.IsOpen());
            Assert.NotNull(app2.GetFileSystem(loc));
            app2.Close(loc);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public async Task File_Operations_Go_Through_The_Controller()
    {
        var settings = new InMemorySettings();
        var app = new EdsAppController(settings);
        string srcDir = NewDir(), dstDir = NewDir();
        try
        {
            var sfs = new StdFs(srcDir);
            var dfs = new StdFs(dstDir);
            WriteFile(sfs, "/a.txt", "payload");

            var res = await app.CopyAsync(
                new[] { sfs.GetPath("/a.txt") }, dfs.GetRootPath().GetDirectory());

            Assert.True(res.Success);
            Assert.Equal("payload", ReadFile(dfs, "/a.txt"));
        }
        finally { TryDeleteDir(srcDir); TryDeleteDir(dstDir); }
    }

    [Fact]
    public async Task EncFs_Location_Through_Controller()
    {
        var settings = new InMemorySettings();
        var pw = "encfs-pw"u8.ToArray();
        string dir = NewDir();
        try
        {
            var cfg = new Config();
            cfg.InitNew("t");
            cfg.KdfIterations = 2000;
            var efs = new EncFsFs(new StdFs(dir).GetRootPath(), cfg, pw);
            try { WriteFile(efs, "/note.txt", "encfs via controller"); }
            finally { efs.Close(false); }

            var app = new EdsAppController(settings);
            var loc = app.AddEncFsLocation(dir);
            await app.OpenAsync(loc, new SecureBuffer(pw));

            Assert.Equal("encfs via controller", ReadFile(app.GetFileSystem(loc), "/note.txt"));
            app.Close(loc);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public async Task Change_Container_Password_Through_Controller()
    {
        var settings = new InMemorySettings();
        var app = new EdsAppController(settings);
        var oldPw = "old-pw"u8.ToArray();
        var newPw = "new-pw"u8.ToArray();
        var (dir, file) = NewVeraCryptContainer(oldPw);
        try
        {
            var loc = app.AddContainerLocation(file);
            await app.OpenAsync(loc, new SecureBuffer(oldPw), pim: Pim); // read-write
            WriteFile(app.GetFileSystem(loc), "/d.txt", "keeps data");

            await app.ChangeContainerPasswordAsync(loc, new SecureBuffer(newPw), newPim: Pim);
            app.Close(loc);

            // New password opens and the data is intact.
            await app.OpenAsync(loc, new SecureBuffer(newPw), pim: Pim);
            Assert.Equal("keeps data", ReadFile(app.GetFileSystem(loc), "/d.txt"));
            app.Close(loc);

            // Old password no longer opens.
            await Assert.ThrowsAsync<WrongPasswordException>(
                () => app.OpenAsync(loc, new SecureBuffer(oldPw), pim: Pim));
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public async Task Create_Container_Through_Controller_Then_Open()
    {
        var settings = new InMemorySettings();
        var app = new EdsAppController(settings);
        var pw = "pw"u8.ToArray();
        string dir = NewDir();
        string file = Path.Combine(dir, "created.hc");
        try
        {
            var loc = await app.CreateContainerAsync(file, new SecureBuffer(pw), new ContainerCreator.Options
            {
                Format = ContainerCreator.Format.VeraCrypt,
                Cipher = ContainerCreator.Cipher.Aes,
                Hash = ContainerCreator.Hash.Sha512,
                VolumeSize = 8 * 1024 * 1024,
                FormatFat = true,
                Pim = Pim,
            });

            Assert.NotNull(loc);
            await app.OpenAsync(loc!, new SecureBuffer(pw), pim: Pim);
            Assert.True(loc!.IsOpen());

            WriteFile(app.GetFileSystem(loc), "/n.txt", "freshly created");
            Assert.Equal("freshly created", ReadFile(app.GetFileSystem(loc), "/n.txt"));
            app.Close(loc);
        }
        finally { TryDeleteDir(dir); }
    }

    // ---- helpers -------------------------------------------------------

    private static (string dir, string file) NewVeraCryptContainer(byte[] password)
    {
        string dir = NewDir();
        string file = Path.Combine(dir, "vol.hc");
        ContainerCreator.Create(file, password, new ContainerCreator.Options
        {
            Format = ContainerCreator.Format.VeraCrypt,
            Cipher = ContainerCreator.Cipher.Aes,
            Hash = ContainerCreator.Hash.Sha512,
            VolumeSize = 8 * 1024 * 1024,
            FormatFat = true,
            Pim = Pim,
        });
        return (dir, file);
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"eds_app_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(IFileSystem fs, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var io = fs.GetPath(path).GetFile().GetRandomAccessIO(FileAccessMode.ReadWriteTruncate);
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
}
