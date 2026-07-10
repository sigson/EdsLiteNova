using System.Security.Cryptography;
using System.Text;
using Eds.Core.Containers;
using Eds.Core.Crypto;
using Eds.Core.Exceptions;
using Eds.Core.Fs;
using Eds.Core.Fs.Fat;
using Eds.Core.Fs.Vfs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase G: container format completeness — keyfiles, VeraCrypt PIM, and
/// change-password. Uses VeraCrypt with a small PIM (~20k KDF iterations) so every
/// open stays fast.
///
/// <para>Note: these prove the port is internally consistent (create + open with
/// the same secrets agree). Byte-exact parity with volumes produced by real
/// VeraCrypt — especially the keyfile pool — must be confirmed with reference
/// artifacts (cross-task K1).</para>
/// </summary>
public class ContainerFormatTests
{
    private const int Pim = 5;
    private const long VolSize = 8 * 1024 * 1024;

    // ---- keyfiles ------------------------------------------------------

    [Fact]
    public void Keyfile_Open_Succeeds_With_Keyfile_And_Fails_Without()
    {
        var pw = "pw"u8.ToArray();
        var keyfile = RandomBytes(137);
        string file = NewFile();
        try
        {
            Create(file, pw, kf: keyfile);

            // Correct password + keyfile → opens, and the FAT is readable/writable.
            using (var io = StreamRandomAccessIO.OpenFile(file, writable: true))
            using (var c = new EdsContainer(io))
            {
                Assert.True(c.Open(pw, Options(keyfile)));
                var vol = c.GetCachedEncryptedVolume();
                var fat = new FatVfs(FatFileSystem.Mount(vol), writable: true);
                Write(fat, "/k.txt", "keyed data");
                Assert.Equal("keyed data", Read(fat, "/k.txt"));
                vol.Flush();
            }

            // Same password, no keyfile → fails.
            Assert.False(TryOpen(file, pw, new ContainerOpenOptions { Pim = Pim }));

            // Same password, wrong keyfile → fails.
            Assert.False(TryOpen(file, pw, Options(RandomBytes(64))));
        }
        finally { Delete(file); }
    }

    [Fact]
    public void Keyfile_Multiple_Files_Order_Independent()
    {
        // The pool is built by byte-wise addition, so keyfile order does not matter.
        var pw = "pw"u8.ToArray();
        var a = RandomBytes(80);
        var b = RandomBytes(200);
        string file = NewFile();
        try
        {
            Create(file, pw, keyfiles: new[] { Keyfiles.FromBytes(a), Keyfiles.FromBytes(b) });
            Assert.True(TryOpen(file, pw, new ContainerOpenOptions
            {
                Pim = Pim,
                Keyfiles = new[] { Keyfiles.FromBytes(b), Keyfiles.FromBytes(a) }, // reversed
            }));
        }
        finally { Delete(file); }
    }

    // ---- PIM -----------------------------------------------------------

    [Fact]
    public void Pim_Wrong_Value_Fails_To_Open()
    {
        var pw = "pw"u8.ToArray();
        string file = NewFile();
        try
        {
            Create(file, pw);
            Assert.True(TryOpen(file, pw, new ContainerOpenOptions { Pim = Pim }));
            Assert.False(TryOpen(file, pw, new ContainerOpenOptions { Pim = Pim + 2 }));
        }
        finally { Delete(file); }
    }

    // ---- change password ----------------------------------------------

    [Fact]
    public void ChangePassword_Reencrypts_Header_Keeping_Data()
    {
        var oldPw = "old-secret"u8.ToArray();
        var newPw = "new-secret"u8.ToArray();
        string file = NewFile();
        try
        {
            Create(file, oldPw);

            // Open, seed data, change the password, close.
            using (var io = StreamRandomAccessIO.OpenFile(file, writable: true))
            using (var c = new EdsContainer(io))
            {
                Assert.True(c.Open(oldPw, new ContainerOpenOptions { Pim = Pim }));
                var vol = c.GetCachedEncryptedVolume();
                var fat = new FatVfs(FatFileSystem.Mount(vol), writable: true);
                Write(fat, "/data.txt", "survives rekey");
                vol.Flush();

                c.ChangePassword(newPw, new ContainerOpenOptions { Pim = Pim });
            }

            // New password opens and the data is intact.
            using (var io = StreamRandomAccessIO.OpenFile(file, writable: false))
            using (var c = new EdsContainer(io))
            {
                Assert.True(c.Open(newPw, new ContainerOpenOptions { Pim = Pim }));
                var fat = new FatVfs(FatFileSystem.Mount(c.GetCachedEncryptedVolume()), writable: false);
                Assert.Equal("survives rekey", Read(fat, "/data.txt"));
            }

            // Old password no longer works.
            Assert.False(TryOpen(file, oldPw, new ContainerOpenOptions { Pim = Pim }));
        }
        finally { Delete(file); }
    }

    [Fact]
    public void ChangePassword_Can_Add_A_Keyfile()
    {
        var pw = "pw"u8.ToArray();
        var keyfile = RandomBytes(99);
        string file = NewFile();
        try
        {
            Create(file, pw); // no keyfile initially

            using (var io = StreamRandomAccessIO.OpenFile(file, writable: true))
            using (var c = new EdsContainer(io))
            {
                Assert.True(c.Open(pw, new ContainerOpenOptions { Pim = Pim }));
                c.ChangePassword(pw, Options(keyfile)); // same password, now with a keyfile
            }

            Assert.True(TryOpen(file, pw, Options(keyfile)));       // password + keyfile
            Assert.False(TryOpen(file, pw, new ContainerOpenOptions { Pim = Pim })); // password alone
        }
        finally { Delete(file); }
    }

    [Fact]
    public void ChangePassword_Works_For_Luks()
    {
        var oldPw = "old-luks"u8.ToArray();
        var newPw = "new-luks"u8.ToArray();
        string file = NewFile();
        try
        {
            ContainerCreator.Create(file, oldPw, new ContainerCreator.Options
            {
                Format = ContainerCreator.Format.Luks,
                Cipher = ContainerCreator.Cipher.Aes,
                Hash = ContainerCreator.Hash.Sha512,
                VolumeSize = VolSize,
                FormatFat = true,
            });

            // Open, seed data, re-key the opening keyslot, close.
            using (var io = StreamRandomAccessIO.OpenFile(file, writable: true))
            using (var c = new EdsContainer(io))
            {
                Assert.True(c.Open(oldPw)); // LUKS: no PIM/keyfiles
                var vol = c.GetCachedEncryptedVolume();
                var fat = new FatVfs(FatFileSystem.Mount(vol), writable: true);
                Write(fat, "/luks.txt", "luks survives rekey");
                vol.Flush();
                c.ChangePassword(newPw);
            }

            // New password opens and the data is intact.
            using (var io = StreamRandomAccessIO.OpenFile(file, writable: false))
            using (var c = new EdsContainer(io))
            {
                Assert.True(c.Open(newPw));
                var fat = new FatVfs(FatFileSystem.Mount(c.GetCachedEncryptedVolume()), writable: false);
                Assert.Equal("luks survives rekey", Read(fat, "/luks.txt"));
            }

            // Old password no longer opens the keyslot (checked directly to avoid the
            // slow VeraCrypt sweep a full EdsContainer.Open would trigger on failure).
            using (var io = StreamRandomAccessIO.OpenFile(file, writable: false))
            {
                var luks = new LuksLayout();
                luks.SetPassword((byte[])oldPw.Clone());
                Assert.Throws<WrongPasswordException>(() => luks.ReadHeader(io));
                luks.Dispose();
            }
        }
        finally { Delete(file); }
    }

    // ---- format metadata ----------------------------------------------

    [Fact]
    public void FormatInfo_Reports_Capabilities()
    {
        Assert.True(ContainerFormats.VeraCrypt.HasKeyfilesSupport);
        Assert.True(ContainerFormats.VeraCrypt.HasCustomKdfIterationsSupport);
        Assert.True(ContainerFormats.TrueCrypt.HasKeyfilesSupport);
        Assert.False(ContainerFormats.TrueCrypt.HasCustomKdfIterationsSupport);
        Assert.False(ContainerFormats.Luks.HasKeyfilesSupport);
        Assert.Equal(64, ContainerFormats.VeraCrypt.MaxPasswordLength);

        Assert.Same(ContainerFormats.VeraCrypt, ContainerFormats.For(ContainerCreator.Format.VeraCrypt));
        // All in open-sweep priority order (LUKS first).
        Assert.Equal(ContainerCreator.Format.Luks, ContainerFormats.All[0].Format);
    }

    // ---- keyfile mixer unit -------------------------------------------

    [Fact]
    public void Keyfile_Crc_Matches_Published_Mpeg2_Check_Value()
    {
        // The keyfile pool CRC must be CRC-32/MPEG-2 (non-reflected). Its published
        // check value — CRC of the ASCII string "123456789" — is 0x0376E6E7. This
        // pins the table byte-for-byte, independent of any container round-trip.
        Assert.Equal(0x0376E6E7u, KeyfileMixer.Crc32Mpeg2("123456789"u8));
        Assert.Equal(0xFFFFFFFFu, KeyfileMixer.Crc32Mpeg2(ReadOnlySpan<byte>.Empty)); // init, no bytes
    }

    [Fact]
    public void Mixer_NoKeyfiles_Is_NoOp_WithKeyfiles_Deterministic()
    {
        var pw = "hunter2"u8.ToArray();

        // No keyfiles → unchanged (a copy, not the same array).
        var same = KeyfileMixer.Apply(pw, null);
        Assert.Equal(pw, same);
        Assert.NotSame(pw, same);

        var kf = new[] { Keyfiles.FromBytes(RandomBytes(300)) };
        var m1 = KeyfileMixer.Apply(pw, kf);
        var m2 = KeyfileMixer.Apply(pw, kf);
        Assert.Equal(m1, m2);                              // deterministic
        Assert.Equal(KeyfileMixer.PoolSize, m1.Length);    // extended to pool size (64)
        Assert.False(pw.AsSpan().SequenceEqual(m1.AsSpan(0, pw.Length))); // first bytes altered
    }

    // ---- helpers -------------------------------------------------------

    private static ContainerOpenOptions Options(byte[] keyfile) => new()
    {
        Pim = Pim,
        Keyfiles = new[] { Keyfiles.FromBytes(keyfile) },
    };

    private static void Create(string file, byte[] password, byte[]? kf = null,
        IReadOnlyList<Func<Stream>>? keyfiles = null)
    {
        ContainerCreator.Create(file, password, new ContainerCreator.Options
        {
            Format = ContainerCreator.Format.VeraCrypt,
            Cipher = ContainerCreator.Cipher.Aes,
            Hash = ContainerCreator.Hash.Sha512,
            VolumeSize = VolSize,
            FormatFat = true,
            Pim = Pim,
            Keyfiles = keyfiles ?? (kf == null ? null : new[] { Keyfiles.FromBytes(kf) }),
        });
    }

    private static bool TryOpen(string file, byte[] password, ContainerOpenOptions options)
    {
        using var io = StreamRandomAccessIO.OpenFile(file, writable: false);
        using var c = new EdsContainer(io);
        return c.Open(password, options);
    }

    private static void Write(IFileSystem fs, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var io = fs.GetPath(path).GetFile().GetRandomAccessIO(FileAccessMode.ReadWriteTruncate);
        io.Seek(0);
        io.Write(bytes, 0, bytes.Length);
        io.Flush();
    }

    private static string Read(IFileSystem fs, string path)
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

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static string NewFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"eds_fmt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "vol.hc");
    }

    private static void Delete(string file)
    {
        try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch { /* ignore */ }
    }
}
