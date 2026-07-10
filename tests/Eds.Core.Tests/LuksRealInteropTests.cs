using System.Text;
using Eds.Core.Containers;
using Eds.Core.Exceptions;
using Eds.Core.Fs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Cross-task K1 (data compatibility) for LUKS1: opens headers produced by the
/// <b>real cryptsetup 2.7.0</b> tool and confirms the port derives a working
/// master key. A successful open is not superficial — <see cref="LuksLayout"/>
/// only returns true after PBKDF2 (per keyslot) -> cipher-decrypt of the AF key
/// material -> AF-merge -> and a match against the header's master-key digest that
/// cryptsetup itself wrote. So opening a real header with the correct password
/// proves the whole KDF/AF/digest chain is byte-compatible; the wrong password is
/// rejected by the same digest check (a <see cref="WrongPasswordException"/>).
///
/// Fixtures under <c>fixtures/interop/luks</c> are the header + keyslot-0 AF
/// material of real volumes (truncated before the 2 MiB payload to stay small).
/// Provenance and the independently-verified master keys are in
/// <c>fixtures/interop/MANIFEST.md</c>; the harness under
/// <c>tests/interop-verification</c> reproduces the exact master-key bytes
/// cryptsetup reports, using the same libedscrypto primitives.
///
/// The layout is driven directly (rather than through <see cref="EdsContainer"/>)
/// so a wrong password fails fast on the LUKS path instead of falling through to
/// the much slower VeraCrypt KDF sweep. Password for all fixtures: <c>testpass123</c>.
/// Only AES-based volumes are present because the sandbox that generated them
/// lacked kernel dm-crypt modules for serpent/twofish; those ciphers are covered
/// by published KAT vectors elsewhere.
/// </summary>
public class LuksRealInteropTests
{
    private const string Correct = "testpass123";
    private const string Wrong = "nope-wrong-pw";

    public static IEnumerable<object[]> Volumes() => new[]
    {
        new object[] { "aes-xts-sha256-512.luks" }, // AES-256 XTS (512-bit key), SHA-256
        new object[] { "aes-xts-sha512-512.luks" }, // AES-256 XTS (512-bit key), SHA-512
        new object[] { "aes-xts-sha1-256.luks" },   // AES-128 XTS (256-bit key), SHA-1
    };

    private static string FixturePath(string name)
        => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "interop", "luks", name);

    [Theory]
    [MemberData(nameof(Volumes))]
    public void Opens_Real_Cryptsetup_Header_With_Correct_Password(string name)
    {
        string path = FixturePath(name);
        Assert.True(System.IO.File.Exists(path), $"LUKS interop fixture missing at {path}");

        using var io = StreamRandomAccessIO.OpenFile(path, writable: false);
        using var layout = new LuksLayout();
        layout.SetPassword(Encoding.UTF8.GetBytes(Correct));
        Assert.True(layout.ReadHeader(io), $"failed to open real LUKS header {name}");
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public void Rejects_Wrong_Password(string name)
    {
        using var io = StreamRandomAccessIO.OpenFile(FixturePath(name), writable: false);
        using var layout = new LuksLayout();
        layout.SetPassword(Encoding.UTF8.GetBytes(Wrong));
        Assert.Throws<WrongPasswordException>(() => layout.ReadHeader(io));
    }

    [Fact]
    public void Opens_Through_EdsContainer_Facade()
    {
        // The full facade path (LUKS is tried first, before VeraCrypt/TrueCrypt).
        using var io = StreamRandomAccessIO.OpenFile(FixturePath("aes-xts-sha256-512.luks"), writable: false);
        using var c = new EdsContainer(io);
        Assert.True(c.Open(Encoding.UTF8.GetBytes(Correct)));
    }
}
