using System.Security.Cryptography;
using System.Text;
using Eds.Core.Fs;
using Eds.Core.Fs.EncFs;
using Eds.Core.Fs.Std;
using Eds.Core.Fs.Vfs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Cross-task K1 (data compatibility) for EncFS: opens a volume that was created
/// by the <b>real desktop encfs 1.9.5</b> tool (standard mode: ssl/aes, nameio/block,
/// keySize 192, blockSize 1024, uniqueIV + chainedNameIV) and reads back the known
/// plaintext. This is genuine interop — not the internal round-trip of
/// <see cref="EncFsIntegrationTests"/>. The reference tree lives under
/// <c>fixtures/interop/encfs-standard</c> (see <c>fixtures/interop/MANIFEST.md</c>
/// for provenance and the verification harness under
/// <c>tests/interop-verification</c> that independently reproduces the decode with
/// the same native primitives).
///
/// Password for all interop fixtures: <c>testpass123</c>.
/// </summary>
public class EncFsRealInteropTests
{
    private const string Password = "testpass123";

    private static readonly (string Path, byte[] Content)[] ExpectedFiles =
    {
        ("readme.txt",  Encoding.UTF8.GetBytes("hello encfs interop")),
        ("empty.dat",   Array.Empty<byte>()),
        ("sub/note.md", Encoding.UTF8.GetBytes("nested file contents here")),
    };

    // 700 lines "L0000\n".."L0699\n"
    private static byte[] LinesContent()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 700; i++) sb.Append('L').Append(i.ToString("D4")).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // sub/deep/data.bin (4096 bytes) — verified by SHA-256 (content is random).
    private const string DataBinSha256 =
        "b698ea901044341f5224728628ed6fe92d03c4d23ba216cc18936584be729f06";

    private static string FixtureRoot()
        => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "interop", "encfs-standard");

    /// <summary>Copies the read-only fixture into a fresh temp dir so the test never mutates it.</summary>
    private static string CopyFixtureToTemp()
    {
        string src = FixtureRoot();
        Assert.True(System.IO.Directory.Exists(src), $"EncFS interop fixture missing at {src}");
        string dst = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eds_encfs_interop_{Guid.NewGuid():N}");
        CopyDir(src, dst);
        return dst;
    }

    private static void CopyDir(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var f in System.IO.Directory.GetFiles(src))
            System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
        foreach (var d in System.IO.Directory.GetDirectories(src))
            CopyDir(d, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(d)));
    }

    private static byte[] ReadAll(IFile file)
    {
        long size = file.GetSize();
        var buf = new byte[size];
        if (size == 0) return buf;
        using var io = file.GetRandomAccessIO(FileAccessMode.Read);
        io.Seek(0);
        int total = 0, n;
        while (total < buf.Length && (n = io.Read(buf, total, buf.Length - total)) > 0) total += n;
        Assert.Equal(buf.Length, total);
        return buf;
    }

    [Fact]
    public void Opens_Real_Encfs_Volume_And_Reads_Files()
    {
        string root = CopyFixtureToTemp();
        try
        {
            var efs = new EncFsFs(new StdFs(root).GetRootPath(), Encoding.UTF8.GetBytes(Password));
            try
            {
                foreach (var (path, content) in ExpectedFiles)
                {
                    IPath p = Resolve(efs, path);
                    Assert.True(p.IsFile(), $"{path} is not a file");
                    var got = ReadAll(p.GetFile());
                    Assert.Equal(content, got);
                }

                // multi-block + partial-tail file
                var lines = ReadAll(Resolve(efs, "lines.txt").GetFile());
                Assert.Equal(LinesContent(), lines);

                // multi-block random file — compare by hash
                var data = ReadAll(Resolve(efs, "sub/deep/data.bin").GetFile());
                Assert.Equal(4096, data.Length);
                Assert.Equal(DataBinSha256, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
            }
            finally { efs.Close(false); }
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Lists_Decrypted_Names_At_Each_Level()
    {
        string root = CopyFixtureToTemp();
        try
        {
            var efs = new EncFsFs(new StdFs(root).GetRootPath(), Encoding.UTF8.GetBytes(Password));
            try
            {
                var rootNames = DecodedNames(efs.GetRootPath().GetDirectory());
                Assert.Contains("readme.txt", rootNames);
                Assert.Contains("lines.txt", rootNames);
                Assert.Contains("empty.dat", rootNames);
                Assert.Contains("sub", rootNames);
                Assert.DoesNotContain("encfs6.xml", rootNames);   // config filtered out
                Assert.DoesNotContain(".encfs6.xml", rootNames);

                var subNames = DecodedNames(Resolve(efs, "sub").GetDirectory());
                Assert.Contains("note.md", subNames);
                Assert.Contains("deep", subNames);
            }
            finally { efs.Close(false); }
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Wrong_Password_Is_Rejected()
    {
        string root = CopyFixtureToTemp();
        try
        {
            Assert.ThrowsAny<Exception>(() =>
                new EncFsFs(new StdFs(root).GetRootPath(), Encoding.UTF8.GetBytes("wrong-password")));
        }
        finally { TryDelete(root); }
    }

    /// <summary>
    /// Resolves a decrypted path by enumerating each directory level and matching the
    /// decrypted child name. This exercises only the decode direction (real ciphertext
    /// → plaintext), so it is a pure interop check independent of name *encoding*.
    /// </summary>
    private static IPath Resolve(EncFsFs efs, string decodedPath)
    {
        IPath current = efs.GetRootPath();
        foreach (var part in decodedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            IPath? next = null;
            using (var contents = current.GetDirectory().List())
                foreach (var child in contents)
                    if (((EncFsPath)child).GetDecodedPath().GetFileName() == part) { next = child; break; }
            Assert.True(next != null, $"could not resolve '{part}' in '{decodedPath}'");
            current = next!;
        }
        return current;
    }

    private static List<string> DecodedNames(IDirectory dir)
    {
        var names = new List<string>();
        using var contents = dir.List();
        foreach (var p in contents)
            names.Add(((EncFsPath)p).GetDecodedPath().GetFileName());
        return names;
    }

    private static void TryDelete(string dir)
    {
        try { System.IO.Directory.Delete(dir, true); } catch { /* best effort */ }
    }
}
