using System.Text;
using Eds.Core.Crypto;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase F prep: the mutable secure password buffer used by password entry.
/// </summary>
public class SecureInputTests
{
    [Fact]
    public void Append_Insert_Delete_Produce_Expected_Content()
    {
        using var b = new EditableSecureBuffer(4);
        b.Append("pass");
        Assert.Equal("pass", Str(b));

        b.Insert(2, 'X');            // pa|X|ss
        Assert.Equal("paXss", Str(b));

        b.Delete(0);                 // remove 'p'
        Assert.Equal("aXss", Str(b));

        b.Delete(1, 2);              // remove "Xs"
        Assert.Equal("as", Str(b));
        Assert.Equal(2, b.Length);
        Assert.Equal('a', b[0]);
    }

    [Fact]
    public void Grows_Beyond_Initial_Capacity_Preserving_Content()
    {
        using var b = new EditableSecureBuffer(2);
        b.Append("abcdefghij"); // forces several reallocations
        Assert.Equal("abcdefghij", Str(b));
        Assert.Equal(10, b.Length);
    }

    [Fact]
    public void ToSecureBuffer_Encodes_Utf8()
    {
        using var b = new EditableSecureBuffer();
        b.Append("héllo·世界");
        using var sb = b.ToSecureBuffer();
        Assert.Equal(Encoding.UTF8.GetBytes("héllo·世界"), sb.GetBytes());
    }

    [Fact]
    public void Clear_Empties_The_Buffer()
    {
        using var b = new EditableSecureBuffer();
        b.Append("secret");
        b.Clear();
        Assert.Equal(0, b.Length);
        using var sb = b.ToSecureBuffer();
        Assert.Equal(0, sb.Length);
    }

    [Fact]
    public void Disposed_Buffer_Rejects_Mutation()
    {
        var b = new EditableSecureBuffer();
        b.Append("x");
        b.Dispose();
        Assert.Throws<ObjectDisposedException>(() => b.Append('y'));
    }

    [Fact]
    public void CloseAll_Disposes_Live_Buffers()
    {
        var a = new EditableSecureBuffer();
        var b = new EditableSecureBuffer();
        a.Append("a");
        b.Append("b");

        EditableSecureBuffer.CloseAll();

        Assert.Throws<ObjectDisposedException>(() => a.Append('z'));
        Assert.Throws<ObjectDisposedException>(() => b.Append('z'));
    }

    [Fact]
    public void Insert_Bounds_Are_Checked()
    {
        using var b = new EditableSecureBuffer();
        b.Append("ab");
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Insert(3, 'x'));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.Delete(2));
        b.Insert(2, '!'); // inserting at the end is allowed
        Assert.Equal("ab!", Str(b));
    }

    private static string Str(EditableSecureBuffer b) => new(b.AsReadOnlySpan());
}
