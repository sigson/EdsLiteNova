using System.Text;
using Eds.Core.Containers;
using Eds.Core.Containers.Locations;
using Eds.Core.Crypto;
using Eds.Core.Crypto.BlockCiphers;
using Eds.Core.Crypto.Engines;
using Eds.Core.Crypto.Hash;
using Eds.Core.Crypto.Kdf;
using Eds.Core.Crypto.Native;
using Eds.Core.Fs;
using Eds.Core.Fs.Vfs;
using Eds.Core.Locations;
using Eds.Core.Settings;

namespace Eds.ConsoleHost;

/// <summary>
/// Cross-platform debug harness. Run on Windows/Linux/macOS desktop -
/// no Android emulator required. It exercises the whole managed -> native
/// pipeline and performs a full container create/open/read/write round-trip.
///
/// Usage:
///   eds selftest                 run all self-tests (default)
///   eds open &lt;file&gt; &lt;password&gt;    open a real TrueCrypt/VeraCrypt container
/// </summary>
internal static class Program
{
    private static int _fail;

    private static int Main(string[] args)
    {
        NativeLibraryResolver.EnsureRegistered();

        var cmd = args.Length > 0 ? args[0] : "selftest";
        return cmd switch
        {
            "selftest" => SelfTest(),
            "open" when args.Length >= 3 => OpenContainer(args[1], args[2]),
            "ls" when args.Length >= 3 => ListFat(args[1], args[2], args.Length >= 4 ? args[3] : "/"),
            "cat" when args.Length >= 4 => CatFat(args[1], args[2], args[3]),
            "locations" => LocationsDemo(),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  eds selftest");
        Console.WriteLine("  eds open <container-file> <password>");
        Console.WriteLine("  eds ls   <container-file> <password> [path]   (mount FAT, list dir)");
        Console.WriteLine("  eds cat  <container-file> <password> <path>   (mount FAT, print file)");
        Console.WriteLine("  eds locations                                 (Phase D end-to-end demo)");
        return 2;
    }

    private static (EdsContainer, Eds.Core.Crypto.EncryptedFile)? OpenVolume(string file, string password)
    {
        var baseIo = StreamRandomAccessIO.OpenFile(file, writable: false);
        var container = new EdsContainer(baseIo);
        if (!container.Open(Encoding.UTF8.GetBytes(password)))
        {
            container.Dispose();
            Console.WriteLine("Failed: wrong password or unsupported format.");
            return null;
        }
        return (container, container.GetEncryptedVolume());
    }

    private static int ListFat(string file, string password, string path)
    {
        var opened = OpenVolume(file, password);
        if (opened == null) return 1;
        var (container, vol) = opened.Value;
        try
        {
            var fs = Eds.Core.Fs.Fat.FatFileSystem.Mount(vol);
            Console.WriteLine($"Mounted {fs.Type}, cluster {fs.ClusterSize} bytes, ~{fs.TotalSize / 1024} KiB");
            var entry = path is "/" or "" ? null : fs.ResolvePath(path);
            var list = entry == null ? fs.ListRoot() : fs.ListDirectory(entry);
            foreach (var e in list)
                Console.WriteLine($"  {(e.IsDirectory ? "d" : "-")} {e.Size,10}  {e.Name}");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("Error: " + ex.Message); return 1; }
        finally { vol.Dispose(); container.Dispose(); }
    }

    private static int CatFat(string file, string password, string path)
    {
        var opened = OpenVolume(file, password);
        if (opened == null) return 1;
        var (container, vol) = opened.Value;
        try
        {
            var fs = Eds.Core.Fs.Fat.FatFileSystem.Mount(vol);
            var entry = fs.ResolvePath(path);
            if (entry == null || entry.IsDirectory) { Console.WriteLine("Not a file: " + path); return 1; }
            var bytes = fs.ReadAllBytes(entry);
            Console.Out.Write(Encoding.UTF8.GetString(bytes));
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("Error: " + ex.Message); return 1; }
        finally { vol.Dispose(); container.Dispose(); }
    }

    /// <summary>
    /// Phase D milestone in miniature: create a container with a FAT filesystem,
    /// register it as a location, persist it, then — with a brand-new manager over
    /// the same settings (a simulated restart) — reload it, open it by password,
    /// mount its filesystem, write and read a file, and close. All through the
    /// locations layer, no direct container plumbing.
    /// </summary>
    private static int LocationsDemo()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"eds_locdemo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "vault.hc");
        var password = "demo password"u8.ToArray();
        var payload = "hello from a registered location"u8.ToArray();
        try
        {
            Console.WriteLine($"Creating container at {file} ...");
            ContainerCreator.Create(file, password, new ContainerCreator.Options
            {
                Format = ContainerCreator.Format.TrueCrypt,
                Cipher = ContainerCreator.Cipher.Aes,
                Hash = ContainerCreator.Hash.Sha512,
                VolumeSize = 8 * 1024 * 1024,
                FormatFat = true,
            });

            var settings = new InMemorySettings();

            // Register + persist.
            var mgr = new LocationsManager(settings).RegisterCoreFactories();
            mgr.RegisterFactory(new ContainerLocationFactory());
            var baseLoc = new DeviceLocation(settings, dir, "/" + Path.GetFileName(file));
            var loc = new ContainerLocation(settings, baseLoc);
            mgr.AddNewLocation(loc, store: true);
            Console.WriteLine("Registered and stored location: " + loc.GetLocationUri());

            // Simulated restart: fresh manager, same settings.
            var mgr2 = new LocationsManager(settings).RegisterCoreFactories();
            mgr2.RegisterFactory(new ContainerLocationFactory());
            mgr2.LoadStoredLocations();
            var eds = mgr2.GetLoadedLocations(false).OfType<IEdsLocation>().Single();
            Console.WriteLine("Reloaded location id: " + eds.GetId());

            eds.SetPassword(new SecureBuffer(password));
            eds.Open();
            Console.WriteLine("Opened. Mounting filesystem ...");

            var fs = eds.GetFileSystem();
            var f = fs.GetRootPath().GetDirectory().CreateFile("note.txt");
            using (var io = f.GetRandomAccessIO(FileAccessMode.ReadWrite))
            {
                io.Seek(0);
                io.Write(payload, 0, payload.Length);
                io.Flush();
            }
            mgr2.CloseLocation(eds, force: false);

            // Independent verification through yet another manager.
            var mgr3 = new LocationsManager(settings).RegisterCoreFactories();
            mgr3.RegisterFactory(new ContainerLocationFactory());
            mgr3.LoadStoredLocations();
            var eds3 = mgr3.GetLoadedLocations(false).OfType<IEdsLocation>().Single();
            eds3.SetPassword(new SecureBuffer(password));
            eds3.Open();
            var read = ReadWhole(eds3.GetFileSystem().GetRootPath().Combine("note.txt").GetFile());
            mgr3.CloseLocation(eds3, force: false);

            bool ok = read.AsSpan().SequenceEqual(payload);
            Console.WriteLine("Read back: \"" + Encoding.UTF8.GetString(read) + "\"");
            Console.WriteLine(ok ? "Phase D end-to-end: OK" : "Phase D end-to-end: MISMATCH");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static byte[] ReadWhole(IFile file)
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

    private static int SelfTest()
    {
        Console.WriteLine($"=== EDS Lite managed self-test (native version {NativeLibraryResolver.NativeVersion}) ===\n");

        TestAes();
        TestHashes();
        TestXts();
        TestCbc();
        TestPbkdf2();
        TestContainerRoundTrip();
        TestLuksRoundTrip();

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "ALL PASSED" : $"FAILURES: {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ---- helpers ------------------------------------------------------
    private static void Ok(string name) => Console.WriteLine($"  [ OK ] {name}");
    private static void Fail(string name, string? detail = null)
    {
        Console.WriteLine($"  [FAIL] {name}{(detail != null ? "\n         " + detail : "")}");
        _fail++;
    }
    private static void Check(string name, bool cond) { if (cond) Ok(name); else Fail(name); }
    private static void CheckHex(string name, byte[] got, string expect)
    {
        var g = Convert.ToHexString(got).ToLowerInvariant();
        if (g == expect.ToLowerInvariant()) Ok(name);
        else Fail(name, $"got {g}\n         exp {expect}");
    }

    // ---- AES ----------------------------------------------------------
    private static void TestAes()
    {
        Console.WriteLine("AES (through P/Invoke):");
        var key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var blk = Convert.FromHexString("00112233445566778899aabbccddeeff");
        using var aes = new Aes(32);
        aes.Init(key);
        aes.EncryptBlock(blk);
        CheckHex("AES-256 encrypt (FIPS-197)", blk, "8ea2b7ca516745bfeafc49904b496089");
        aes.DecryptBlock(blk);
        CheckHex("AES-256 decrypt round-trip", blk, "00112233445566778899aabbccddeeff");
    }

    // ---- hashes -------------------------------------------------------
    private static void TestHashes()
    {
        Console.WriteLine("Hashes (through P/Invoke):");
        using var rmd = new Ripemd160();
        CheckHex("RIPEMD160(\"\")", rmd.DoFinal(Array.Empty<byte>()), "9c1185a5c5e9fc54612808977ee8f548b2258d31");
        using var rmd2 = new Ripemd160();
        CheckHex("RIPEMD160(\"abc\")", rmd2.DoFinal("abc"u8.ToArray()), "8eb208f7e05d987a9b044a8e98c6b087f15a0bfc");

        using var wp = new Whirlpool();
        CheckHex("Whirlpool(\"\")", wp.DoFinal(Array.Empty<byte>()),
            "19fa61d75522a4669b44e39c1d2e1726c530232130d407f89afee0964997f7a73e83be698b288febcf88e3e03c4f0757ea8964e59b63d93708b138cc42a66eb3");
    }

    // ---- XTS ----------------------------------------------------------
    private static void TestXts()
    {
        Console.WriteLine("AES-XTS (through P/Invoke):");
        var key = new byte[64];
        for (int i = 0; i < 64; i++) key[i] = (byte)(i + 1);
        using var xts = new AesXts();
        xts.SetKey(key);
        xts.Init();

        var data = new byte[1024];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xff);
        var orig = (byte[])data.Clone();

        xts.SetIV(SectorIv(0));
        xts.Encrypt(data, 0, data.Length);
        Check("XTS ciphertext != plaintext", !data.AsSpan().SequenceEqual(orig));

        xts.SetIV(SectorIv(0));
        xts.Decrypt(data, 0, data.Length);
        Check("XTS decrypt round-trip", data.AsSpan().SequenceEqual(orig));
    }

    private static byte[] SectorIv(long sector)
    {
        var iv = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv, sector);
        return iv;
    }

    // ---- CBC (NIST SP 800-38A, AES-256) -------------------------------
    private static void TestCbc()
    {
        Console.WriteLine("AES-CBC (NIST SP 800-38A, AES-256, through P/Invoke):");
        // F.2.5/F.2.6 CBC-AES256 vectors
        var key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        var iv = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        var pt = Convert.FromHexString(
            "6bc1bee22e409f96e93d7e117393172a" +
            "ae2d8a571e03ac9c9eb76fac45af8e51" +
            "30c81c46a35ce411e5fbc1191a0a52ef" +
            "f69f2445df4f9b17ad2b417be66c3710");
        using var cbc = new Eds.Core.Crypto.Engines.AesCbc();
        cbc.SetKey(key);
        cbc.Init();
        var buf = (byte[])pt.Clone();
        cbc.SetIV(iv);
        cbc.Encrypt(buf, 0, buf.Length);
        CheckHex("CBC encrypt (4 blocks)", buf,
            "f58c4c04d6e5f1ba779eabfb5f7bfbd6" +
            "9cfc4e967edb808d679f777bc6702c7d" +
            "39f23369a9d9bacfa530e26304231461" +
            "b2eb05e2c39be9fcda6c19078c6a9d1b");
        cbc.SetIV(iv);
        cbc.Decrypt(buf, 0, buf.Length);
        Check("CBC decrypt round-trip", buf.AsSpan().SequenceEqual(pt));
    }

    // ---- PBKDF2 (RFC 6070, HMAC-SHA1) ---------------------------------
    private static void TestPbkdf2()
    {
        Console.WriteLine("PBKDF2 (RFC 6070, HMAC-SHA1):");
        var kdf = new HashBasedPbkdf2(BclDigest.Sha1(), 64);
        var pass = "password"u8.ToArray();
        var salt = "salt"u8.ToArray();
        CheckHex("c=1, dkLen=20", kdf.DeriveKey(pass, salt, 1, 20), "0c60c80f961f0e71f3a9b524af6012062fe037a6");
        var kdf2 = new HashBasedPbkdf2(BclDigest.Sha1(), 64);
        CheckHex("c=2, dkLen=20", kdf2.DeriveKey(pass, salt, 2, 20), "ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957");
    }

    // ---- container create + open + read/write round-trip --------------
    private static void TestContainerRoundTrip()
    {
        Console.WriteLine("Container round-trip (TrueCrypt layout, create -> reopen -> verify):");
        string tmp = Path.Combine(Path.GetTempPath(), $"eds_test_{Guid.NewGuid():N}.tc");
        const long containerSize = 512 * 1024;
        var password = "correct horse battery staple"u8.ToArray();
        var payload = Encoding.UTF8.GetBytes("EDS Lite .NET port — end-to-end payload check ✔");

        try
        {
            // create
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, writable: true))
            {
                var layout = new StdLayout();
                layout.SetPassword((byte[])password.Clone());
                layout.FormatNew(baseIo, containerSize);
                using var vol = new Eds.Core.Crypto.EncryptedFile(baseIo, layout);
                vol.Seek(0);
                vol.Write(payload, 0, payload.Length);
                vol.Flush();
                layout.Dispose();
            }

            // reopen
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, writable: false))
            using (var container = new EdsContainer(baseIo))
            {
                bool opened = container.Open(password);
                Check("container opened with correct password", opened);
                if (opened)
                {
                    using var vol = container.GetEncryptedVolume();
                    var read = new byte[payload.Length];
                    vol.Seek(0);
                    int n = vol.Read(read, 0, read.Length);
                    Check("payload read back matches", n == read.Length && read.AsSpan().SequenceEqual(payload));
                }
            }

            // wrong password must fail
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, writable: false))
            using (var container = new EdsContainer(baseIo))
            {
                bool opened = container.Open("wrong password"u8.ToArray());
                Check("wrong password rejected", !opened);
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    // ---- LUKS1 create + open + verify ---------------------------------
    private static void TestLuksRoundTrip()
    {
        Console.WriteLine("LUKS1 round-trip (create -> reopen -> verify):");
        string tmp = Path.Combine(Path.GetTempPath(), $"eds_luks_{Guid.NewGuid():N}.luks");
        const long volumeSize = 256 * 1024;
        var password = "luks test password"u8.ToArray();
        var payload = Encoding.UTF8.GetBytes("LUKS payload ✔ 42");

        try
        {
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, writable: true))
            {
                var layout = new LuksLayout();
                layout.SetPassword((byte[])password.Clone());
                layout.FormatNew(baseIo, volumeSize);
                using var vol = new Eds.Core.Crypto.EncryptedFile(baseIo, layout);
                vol.Seek(0);
                vol.Write(payload, 0, payload.Length);
                vol.Flush();
                layout.Dispose();
            }
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, writable: false))
            using (var container = new EdsContainer(baseIo))
            {
                bool opened = container.Open(password);
                Check("LUKS container opened", opened);
                if (opened)
                {
                    using var vol = container.GetEncryptedVolume();
                    var read = new byte[payload.Length];
                    vol.Seek(0);
                    int n = vol.Read(read, 0, read.Length);
                    Check("LUKS payload matches", n == read.Length && read.AsSpan().SequenceEqual(payload));
                    Check("LUKS cipher is aes-xts-plain64",
                        container.Layout.Engine.CipherName == "AES" &&
                        container.Layout.Engine.CipherModeName == "xts-plain64");
                }
            }
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, writable: false))
            using (var container = new EdsContainer(baseIo))
                Check("LUKS wrong password rejected", !container.Open("nope"u8.ToArray()));
        }
        catch (Exception ex) { Fail("LUKS round-trip", ex.Message); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ---- open a real container ----------------------------------------
    private static int OpenContainer(string file, string password)
    {
        Console.WriteLine($"Opening {file} ...");
        using var baseIo = StreamRandomAccessIO.OpenFile(file, writable: false);
        using var container = new EdsContainer(baseIo);
        var reporter = new DelegateProgressReporter
        {
            OnKdf = k => Console.Write($"\r  trying KDF={k}          "),
            OnCipher = c => Console.Write($"\r  trying cipher={c}       "),
        };
        var pass = Encoding.UTF8.GetBytes(password);
        bool opened = container.Open(pass, reporter);
        Console.WriteLine();
        if (!opened) { Console.WriteLine("  Failed: wrong password or unsupported format."); return 1; }

        Console.WriteLine("  Opened successfully.");
        Console.WriteLine($"  Cipher: {container.Layout.Engine.CipherName}-{container.Layout.Engine.CipherModeName}");
        Console.WriteLine($"  Hash:   {container.Layout.GetHashFunc()?.Algorithm}");
        Console.WriteLine($"  Encrypted data offset: {container.Layout.EncryptedDataOffset}");
        Console.WriteLine("  (Filesystem mounting arrives with the FS layer - see roadmap.)");
        return 0;
    }
}
