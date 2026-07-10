using System.Runtime.InteropServices;
using System.Text;

namespace Eds.Core.Crypto;

/// <summary>
/// A mutable secure buffer of characters for interactive password entry. Port of
/// the intent of <c>crypto.EditableSecureBuffer</c> — the gap guide (§2.7) notes it
/// should be reimplemented over a pinned, wiped <c>char[]</c> rather than copied
/// line-for-line from the 1500-line Java original.
///
/// <para>It backs a password field: characters can be appended, inserted and
/// deleted as the user types, without the value ever landing in an immutable
/// <see cref="string"/>. The backing array is pinned so the GC can't copy it, every
/// freed region (on delete, clear, grow, or dispose) is zeroed, and a process-wide
/// registry allows wiping all live buffers on lock/exit via <see cref="CloseAll"/>.
/// Call <see cref="ToSecureBuffer"/> to obtain the UTF-8 password bytes for the KDF
/// (as a <see cref="SecureBuffer"/> the caller then owns and clears).</para>
/// </summary>
public sealed class EditableSecureBuffer : IDisposable
{
    private static readonly HashSet<EditableSecureBuffer> Registry = new();
    private static readonly object RegistryLock = new();

    private char[]? _chars;
    private GCHandle _handle;
    private int _length;

    public EditableSecureBuffer(int initialCapacity = 32)
    {
        if (initialCapacity < 1) initialCapacity = 1;
        _chars = new char[initialCapacity];
        _handle = GCHandle.Alloc(_chars, GCHandleType.Pinned);
        lock (RegistryLock) Registry.Add(this);
    }

    public int Length => _length;

    private char[] Chars => _chars ?? throw new ObjectDisposedException(nameof(EditableSecureBuffer));

    /// <summary>Reads a character. Bounds-checked.</summary>
    public char this[int index]
    {
        get
        {
            var c = Chars;
            if ((uint)index >= (uint)_length) throw new ArgumentOutOfRangeException(nameof(index));
            return c[index];
        }
    }

    /// <summary>A read-only view of the current characters. Do not retain past a mutation/dispose.</summary>
    public ReadOnlySpan<char> AsReadOnlySpan() => Chars.AsSpan(0, _length);

    public void Append(char c) => Insert(_length, c);

    public void Append(ReadOnlySpan<char> s) => Insert(_length, s);

    public void Insert(int index, char c)
    {
        Span<char> one = stackalloc char[1];
        one[0] = c;
        Insert(index, one);
    }

    public void Insert(int index, ReadOnlySpan<char> s)
    {
        _ = Chars; // dispose guard
        if ((uint)index > (uint)_length) throw new ArgumentOutOfRangeException(nameof(index));
        if (s.Length == 0) return;

        EnsureCapacity(_length + s.Length);
        var buf = _chars!; // re-fetch: EnsureCapacity may have reallocated
        // Shift the tail right to make room.
        Array.Copy(buf, index, buf, index + s.Length, _length - index);
        s.CopyTo(buf.AsSpan(index));
        _length += s.Length;
    }

    /// <summary>Removes <paramref name="count"/> characters at <paramref name="index"/>, zeroing the freed tail.</summary>
    public void Delete(int index, int count = 1)
    {
        var buf = Chars;
        if (count <= 0) return;
        if ((uint)index >= (uint)_length) throw new ArgumentOutOfRangeException(nameof(index));
        count = Math.Min(count, _length - index);

        Array.Copy(buf, index + count, buf, index, _length - index - count);
        int newLength = _length - count;
        buf.AsSpan(newLength, _length - newLength).Clear(); // wipe the now-unused tail
        _length = newLength;
    }

    /// <summary>Zeroes and empties the buffer (keeps the allocation).</summary>
    public void Clear()
    {
        var buf = Chars;
        buf.AsSpan(0, _length).Clear();
        _length = 0;
    }

    /// <summary>Encodes the current characters as UTF-8 into a new <see cref="SecureBuffer"/>.</summary>
    public SecureBuffer ToSecureBuffer()
    {
        var span = AsReadOnlySpan();
        int byteCount = Encoding.UTF8.GetByteCount(span);
        var sb = new SecureBuffer(byteCount);
        if (byteCount > 0) Encoding.UTF8.GetBytes(span, sb.AsSpan());
        return sb;
    }

    private void EnsureCapacity(int required)
    {
        var buf = _chars!;
        if (required <= buf.Length) return;

        int newCap = buf.Length * 2;
        if (newCap < required) newCap = required;

        var newBuf = new char[newCap];
        var newHandle = GCHandle.Alloc(newBuf, GCHandleType.Pinned);
        Array.Copy(buf, newBuf, _length);

        // Wipe and release the old (pinned) array before dropping it.
        Array.Clear(buf);
        if (_handle.IsAllocated) _handle.Free();

        _chars = newBuf;
        _handle = newHandle;
    }

    public void Dispose()
    {
        if (_chars != null) Array.Clear(_chars);
        if (_handle.IsAllocated) _handle.Free();
        _chars = null;
        _length = 0;
        lock (RegistryLock) Registry.Remove(this);
        GC.SuppressFinalize(this);
    }

    ~EditableSecureBuffer() => Dispose();

    /// <summary>Wipes and disposes every live editable buffer (call on lock / app exit).</summary>
    public static void CloseAll()
    {
        EditableSecureBuffer[] all;
        lock (RegistryLock) all = Registry.ToArray();
        foreach (var b in all) b.Dispose();
    }
}
