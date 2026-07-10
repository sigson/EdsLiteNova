using System.Text;
using Eds.Core.Containers;
using Eds.Core.Containers.Locations;
using Eds.Core.Crypto;
using Eds.Core.Exceptions;
using Eds.Core.Fs.EncFs;
using Eds.Core.Fs.Std;
using Eds.Core.Fs.Vfs;
using Eds.Core.Locations;
using Eds.Core.Settings;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase D: locations + settings. Proves the state-of-the-app core — a container
/// or EncFS volume can be registered as a location, persisted, and re-opened by
/// password after a simulated restart, yielding a mounted filesystem — which is
/// the milestone that unblocks the service (E) and UI (F) phases.
/// </summary>
public class LocationsTests
{
    // ---- helpers -------------------------------------------------------

    [Fact]
    public void ContainerLocation_Persists_Keyfile_And_Pim_Across_Restart()
    {
        var settings = new InMemorySettings();
        var password = "pw"u8.ToArray();
        string dir = Path.Combine(Path.GetTempPath(), $"eds_kf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "vol.hc");
        string keyfilePath = Path.Combine(dir, "key.bin");

        var keyfileBytes = new byte[120];
        for (int i = 0; i < keyfileBytes.Length; i++) keyfileBytes[i] = (byte)(i * 5 + 1);
        File.WriteAllBytes(keyfilePath, keyfileBytes);

        try
        {
            // VeraCrypt container protected by password + keyfile + PIM 5.
            ContainerCreator.Create(file, password, new ContainerCreator.Options
            {
                Format = ContainerCreator.Format.VeraCrypt,
                Cipher = ContainerCreator.Cipher.Aes,
                Hash = ContainerCreator.Hash.Sha512,
                VolumeSize = 8 * 1024 * 1024,
                FormatFat = true,
                Pim = 5,
                Keyfiles = new[] { Eds.Core.Containers.Keyfiles.FromBytes(keyfileBytes) },
            });

            // Register + remember the keyfile path and PIM in the location's settings.
            var mgr = NewManager(settings);
            var loc = MakeContainerLocation(settings, dir, file);
            var es = (ContainerLocation.ContainerExternalSettings)loc.GetExternalSettings();
            es.KeyfilePaths = new[] { keyfilePath };
            es.CustomKdfIterations = 5; // PIM
            loc.SaveExternalSettings();
            mgr.AddNewLocation(loc, store: true);

            // Restart: reopen with password only — keyfile + PIM come from settings.
            var mgr2 = NewManager(settings);
            mgr2.LoadStoredLocations();
            var loc2 = mgr2.GetLoadedLocations(false).OfType<IEdsLocation>().Single();
            loc2.SetPassword(new SecureBuffer(password));
            loc2.Open();
            Assert.True(loc2.IsOpen());
            Assert.NotNull(loc2.GetFileSystem());
            mgr2.CloseLocation(loc2, force: false);
        }
        finally { TryDeleteDir(dir); }
    }

    private static LocationsManager NewManager(ISettings settings)
    {
        var mgr = new LocationsManager(settings).RegisterCoreFactories();
        mgr.RegisterFactory(new ContainerLocationFactory());
        return mgr;
    }

    private static (string dir, string file) NewContainer(byte[] password, ContainerCreator.Format format)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"eds_loc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "volume.hc");
        ContainerCreator.Create(file, password, new ContainerCreator.Options
        {
            Format = format,
            Cipher = ContainerCreator.Cipher.Aes,
            Hash = ContainerCreator.Hash.Sha512,
            VolumeSize = 8 * 1024 * 1024,
            FormatFat = true,
        });
        return (dir, file);
    }

    private static ContainerLocation MakeContainerLocation(ISettings settings, string dir, string file)
    {
        var baseLoc = new DeviceLocation(settings, dir, "/" + Path.GetFileName(file));
        return new ContainerLocation(settings, baseLoc);
    }

    // ---- URI -----------------------------------------------------------

    [Fact]
    public void LocationUri_RoundTrips_WithNestedBase()
    {
        var baseUri = new LocationUri("file", "/volume.hc").WithQueryParameter("root", @"/tmp/my dir");
        var contUri = new LocationUri("eds-container", "/inner/path")
            .WithQueryParameter("location", baseUri.ToString());

        var reparsed = LocationUri.Parse(contUri.ToString());
        Assert.Equal(contUri, reparsed);
        Assert.Equal("eds-container", reparsed.Scheme);
        Assert.Equal("/inner/path", reparsed.Path);

        var nested = LocationUri.Parse(reparsed.GetQueryParameter("location")!);
        Assert.Equal("file", nested.Scheme);
        Assert.Equal("/volume.hc", nested.Path);
        Assert.Equal(@"/tmp/my dir", nested.GetQueryParameter("root"));
    }

    // ---- SimpleCrypto --------------------------------------------------

    [Fact]
    public void SimpleCrypto_Md5_Stable_And_Encrypt_RoundTrips()
    {
        Assert.Equal(SimpleCrypto.CalcStringMd5("abc"), SimpleCrypto.CalcStringMd5("abc"));
        Assert.NotEqual(SimpleCrypto.CalcStringMd5("abc"), SimpleCrypto.CalcStringMd5("abd"));

        var key = Encoding.UTF8.GetBytes("protection-key-material");
        var data = Encoding.UTF8.GetBytes("secret container password ✔");
        var blob = SimpleCrypto.Encrypt(key, data);
        Assert.NotEqual(Convert.ToBase64String(data), blob);
        Assert.Equal(data, SimpleCrypto.Decrypt(key, blob));
    }

    // ---- external settings --------------------------------------------

    [Fact]
    public void ExternalSettings_Persist_TitleVisibilityAndSavedPassword()
    {
        var settings = new InMemorySettings();
        var (dir, file) = NewContainer("pw"u8.ToArray(), ContainerCreator.Format.TrueCrypt);
        try
        {
            var provider = new StaticKeyProvider("kek-bytes"u8.ToArray());

            var loc = MakeContainerLocation(settings, dir, file);
            loc.ProtectionKeyProvider = provider;
            var es = (ContainerLocation.ContainerExternalSettings)loc.GetExternalSettings();
            es.Title = "My Vault";
            es.IsVisibleToUser = true;
            es.ContainerFormatName = "TrueCrypt";
            es.SetSavedPassword("saved-secret"u8.ToArray());
            loc.SaveExternalSettings();

            // fresh instance, same settings + key -> everything comes back
            var loc2 = MakeContainerLocation(settings, dir, file);
            loc2.ProtectionKeyProvider = provider;
            var es2 = (ContainerLocation.ContainerExternalSettings)loc2.GetExternalSettings();
            Assert.Equal("My Vault", es2.Title);
            Assert.True(es2.IsVisibleToUser);
            Assert.Equal("TrueCrypt", es2.ContainerFormatName);
            Assert.True(es2.HasSavedPassword);
            Assert.Equal("saved-secret"u8.ToArray(), es2.GetSavedPassword());
        }
        finally { TryDeleteDir(dir); }
    }

    // ---- device location ----------------------------------------------

    [Fact]
    public void DeviceLocation_Uri_RoundTrips_Through_Manager()
    {
        var settings = new InMemorySettings();
        var mgr = NewManager(settings);
        string dir = Path.Combine(Path.GetTempPath(), $"eds_dev_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var dev = new DeviceLocation(settings, dir, "/sub/file.txt");
            var uri = dev.GetLocationUri();
            var recreated = mgr.CreateLocationFromUri(uri)!;
            Assert.Equal(dev.GetId(), recreated.GetId());
            Assert.True(recreated.IsDirectlyAccessible());
            Assert.IsType<StdFs>(recreated.GetFileSystem());
        }
        finally { TryDeleteDir(dir); }
    }

    // ---- container end-to-end -----------------------------------------

    [Theory]
    [InlineData(ContainerCreator.Format.TrueCrypt)]
    [InlineData(ContainerCreator.Format.Luks)]
    public void ContainerLocation_Register_Persist_Reopen_WriteRead(ContainerCreator.Format format)
    {
        var settings = new InMemorySettings();
        var password = "the location password"u8.ToArray();
        var payload = "written through a registered location ✔"u8.ToArray();
        var (dir, file) = NewContainer(password, format);
        try
        {
            // --- register + persist ---
            var mgr = NewManager(settings);
            var loc = MakeContainerLocation(settings, dir, file);
            mgr.AddNewLocation(loc, store: true);
            Assert.True(mgr.IsStoredLocation(loc.GetId()));

            // --- simulate a fresh process: new manager over the same settings ---
            var mgr2 = NewManager(settings);
            mgr2.LoadStoredLocations();
            var edsLoc = mgr2.GetLoadedLocations(false).OfType<IEdsLocation>().Single();

            Assert.False(edsLoc.IsOpen());
            edsLoc.SetPassword(new SecureBuffer(password));
            edsLoc.Open();
            Assert.True(edsLoc.IsOpen());

            var fs = edsLoc.GetFileSystem();
            var rootDir = fs.GetRootPath().GetDirectory();
            var f = rootDir.CreateFile("note.txt");
            using (var io = f.GetRandomAccessIO(FileAccessMode.ReadWrite))
            {
                io.Seek(0);
                io.Write(payload, 0, payload.Length);
                io.Flush();
            }
            mgr2.CloseLocation(edsLoc, force: false);
            Assert.False(edsLoc.IsOpen());

            // --- reopen once more and confirm the write persisted ---
            var mgr3 = NewManager(settings);
            mgr3.LoadStoredLocations();
            var edsLoc3 = mgr3.GetLoadedLocations(false).OfType<IEdsLocation>().Single();
            edsLoc3.SetPassword(new SecureBuffer(password));
            edsLoc3.Open();
            var read = ReadWholeFile(edsLoc3.GetFileSystem().GetRootPath().Combine("note.txt").GetFile());
            Assert.Equal(payload, read);
            mgr3.CloseLocation(edsLoc3, force: false);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void ContainerLocation_WrongPassword_Throws()
    {
        var settings = new InMemorySettings();
        var (dir, file) = NewContainer("right"u8.ToArray(), ContainerCreator.Format.TrueCrypt);
        try
        {
            var loc = MakeContainerLocation(settings, dir, file);
            loc.SetPassword(new SecureBuffer("wrong"u8.ToArray()));
            Assert.Throws<WrongPasswordException>(() => loc.Open());
            Assert.False(loc.IsOpen());
        }
        finally { TryDeleteDir(dir); }
    }

    // ---- EncFS end-to-end ---------------------------------------------

    [Fact]
    public void EncFsLocation_Register_Persist_Reopen_Read()
    {
        var settings = new InMemorySettings();
        var password = "encfs location password"u8.ToArray();
        var payload = new byte[1500];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7 + 3);

        string dir = Path.Combine(Path.GetTempPath(), $"eds_encfsloc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // --- create the EncFS volume and write a file directly ---
            var cfg = new Config();
            cfg.InitNew("eds-test");
            cfg.KdfIterations = 2000;
            var efsCreate = new EncFsFs(new StdFs(dir).GetRootPath(), cfg, password);
            try
            {
                var d = efsCreate.GetRootPath().GetDirectory().CreateDirectory("Docs");
                var fl = d.CreateFile("data.bin");
                using var io = fl.GetRandomAccessIO(FileAccessMode.ReadWrite);
                io.Seek(0); io.Write(payload, 0, payload.Length); io.Flush();
            }
            finally { efsCreate.Close(false); }

            // --- register an EncFS location over that directory + persist ---
            var mgr = NewManager(settings);
            var baseLoc = new DeviceLocation(settings, dir); // current path == root == encfs dir
            var encLoc = new EncFsLocation(settings, baseLoc);
            mgr.AddNewLocation(encLoc, store: true);

            // --- simulate restart and read the file back through the location ---
            var mgr2 = NewManager(settings);
            mgr2.LoadStoredLocations();
            var loc = mgr2.GetLoadedLocations(false).OfType<IEdsLocation>().Single();
            loc.SetPassword(new SecureBuffer(password));
            loc.Open();

            var filePath = loc.GetFileSystem().GetRootPath().Combine("Docs").Combine("data.bin");
            Assert.True(filePath.Exists());
            Assert.Equal(payload, ReadWholeFile(filePath.GetFile()));
            mgr2.CloseLocation(loc, force: false);
        }
        finally { TryDeleteDir(dir); }
    }

    // ---- manager events ------------------------------------------------

    [Fact]
    public void Manager_Raises_AddAndRemove_Events()
    {
        var settings = new InMemorySettings();
        var mgr = NewManager(settings);
        string dir = Path.Combine(Path.GetTempPath(), $"eds_ev_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            ILocation? added = null, removed = null;
            mgr.LocationAdded += (_, e) => added = e.Location;
            mgr.LocationRemoved += (_, e) => removed = e.Location;

            var dev = new DeviceLocation(settings, dir);
            mgr.AddNewLocation(dev, store: false);
            Assert.Same(dev, added);
            Assert.Equal(dev.GetId(), mgr.FindExistingLocation(dev.GetId())!.GetId());

            mgr.RemoveLocation(dev);
            Assert.Same(dev, removed);
            Assert.Null(mgr.FindExistingLocation(dev.GetId()));
        }
        finally { TryDeleteDir(dir); }
    }

    // ---- utils ---------------------------------------------------------

    private static byte[] ReadWholeFile(IFile file)
    {
        using var io = file.GetRandomAccessIO(FileAccessMode.Read);
        long n = file.GetSize();
        var buf = new byte[n];
        int off = 0;
        io.Seek(0);
        while (off < n)
        {
            int r = io.Read(buf, off, (int)(n - off));
            if (r <= 0) break;
            off += r;
        }
        return buf;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }

    private sealed class StaticKeyProvider(byte[] key) : IProtectionKeyProvider
    {
        public SecureBuffer? GetProtectionKey() => new SecureBuffer(key);
    }
}
