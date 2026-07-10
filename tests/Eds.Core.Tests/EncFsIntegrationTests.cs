using System.Text;
using Eds.Core.Fs;
using Eds.Core.Fs.EncFs;
using Eds.Core.Fs.Std;
using Eds.Core.Fs.Vfs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase C end-to-end: create an EncFS volume on a real (StdFs) temp directory,
/// write directories and files through the encrypted view, close, reopen with the
/// password, and read everything back. This exercises the whole EncFS stack:
/// config, master-key derivation, name encoding, per-file IV header, block IO.
/// (Byte-for-byte interop with desktop EncFS is a separate matter — cross-task K1.)
/// </summary>
public class EncFsIntegrationTests
{
    private static string NewTempRoot()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eds_encfs_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static Config NewVolumeConfig(bool chainedNameIV, int macBytes)
    {
        var cfg = new Config();
        cfg.InitNew("eds-test");
        cfg.UseChainedNameIV = chainedNameIV;
        cfg.MacBytes = macBytes;
        cfg.MacRandBytes = 0;
        cfg.KdfIterations = 2000; // keep the test fast
        return cfg;
    }

    private static void RoundTrip(bool chainedNameIV, int macBytes)
    {
        string root = NewTempRoot();
        try
        {
            var password = Encoding.UTF8.GetBytes("correct horse battery staple");
            var payload = new byte[1024 * 2 + 500]; // multiple full blocks + a partial one
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 37 + 11);

            // --- create volume, write a dir + file ---
            var efsCreate = new EncFsFs(new StdFs(root).GetRootPath(), NewVolumeConfig(chainedNameIV, macBytes), password);
            try
            {
                var rootDir = efsCreate.GetRootPath().GetDirectory();
                var sub = rootDir.CreateDirectory("Documents");
                var file = sub.CreateFile("hello.bin");
                using var io = file.GetRandomAccessIO(FileAccessMode.ReadWrite);
                io.Seek(0);
                io.Write(payload, 0, payload.Length);
                io.Flush();
                io.Dispose();
            }
            finally { efsCreate.Close(false); }

            // --- reopen and read back ---
            var efs = new EncFsFs(new StdFs(root).GetRootPath(), password);
            try
            {
                // navigate by decrypted names via Combine (which encodes each part)
                var filePath = efs.GetRootPath().Combine("Documents").Combine("hello.bin");
                Assert.True(filePath.Exists());
                var file = filePath.GetFile();
                Assert.Equal(payload.Length, file.GetSize());

                var read = new byte[payload.Length];
                using (var io = file.GetRandomAccessIO(FileAccessMode.Read))
                {
                    io.Seek(0);
                    int total = 0, n;
                    while (total < read.Length && (n = io.Read(read, total, read.Length - total)) > 0) total += n;
                    Assert.Equal(payload.Length, total);
                }
                Assert.True(read.AsSpan().SequenceEqual(payload));

                // directory listing exposes the decrypted name
                var docs = efs.GetRootPath().Combine("Documents").GetDirectory();
                var names = new List<string>();
                using (var contents = docs.List())
                    foreach (var p in contents)
                        names.Add(((EncFsPath)p).GetDecodedPath().GetFileName());
                Assert.Contains("hello.bin", names);

                // the on-disk name must be encrypted (not the plaintext)
                Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(root, "Documents")));
            }
            finally { efs.Close(false); }
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void EncFs_RoundTrip_NoChainedIV_NoMac() => RoundTrip(chainedNameIV: false, macBytes: 0);

    [Fact]
    public void EncFs_RoundTrip_ChainedIV_NoMac() => RoundTrip(chainedNameIV: true, macBytes: 0);

    [Fact]
    public void EncFs_RoundTrip_ChainedIV_WithBlockMac() => RoundTrip(chainedNameIV: true, macBytes: 8);
}
