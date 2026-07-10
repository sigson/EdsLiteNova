using System.Runtime.InteropServices;
using System.Text;

namespace Eds.Core.Crypto;

/// <summary>
/// A best-effort secure buffer for passwords/keys. Mirrors the intent of
/// <c>crypto.SecureBuffer</c>: keep sensitive bytes out of ordinary GC-managed
/// strings, pin them so the GC can't copy them around, and zero them on dispose.
///
/// Notes (see port guide 9.3): true secure memory is impossible on a managed GC,
/// but pinning + explicit clearing + avoiding <see cref="string"/> gives parity
/// with the original. A process-wide registry allows clearing everything on
/// lock/exit (<see cref="CloseAll"/>).
/// </summary>
public sealed class SecureBuffer : IDisposable
{
    private static readonly HashSet<SecureBuffer> Registry = new();
    private static readonly object RegistryLock = new();

    private byte[]? _data;
    private GCHandle _handle;

    public SecureBuffer(int capacity)
    {
        _data = new byte[capacity];
        _handle = GCHandle.Alloc(_data, GCHandleType.Pinned);
        Register(this);
    }

    public SecureBuffer(byte[] source) : this(source.Length)
    {
        Array.Copy(source, _data!, source.Length);
    }

    /// <summary>Builds a buffer from a password without leaving it in an immutable string.</summary>
    public static SecureBuffer FromPassword(ReadOnlySpan<char> password)
    {
        int byteCount = Encoding.UTF8.GetByteCount(password);
        var buf = new SecureBuffer(byteCount);
        Encoding.UTF8.GetBytes(password, buf._data.AsSpan());
        return buf;
    }

    public int Length => _data?.Length ?? 0;

    /// <summary>Direct view of the underlying bytes (do not retain past dispose).</summary>
    public Span<byte> AsSpan() => _data ?? throw new ObjectDisposedException(nameof(SecureBuffer));

    /// <summary>Returns a plain copy for feeding into APIs that require byte[].</summary>
    public byte[] GetBytes()
    {
        if (_data == null) throw new ObjectDisposedException(nameof(SecureBuffer));
        return (byte[])_data.Clone();
    }

    public void EraseData()
    {
        if (_data != null) Array.Clear(_data);
    }

    public void Dispose()
    {
        EraseData();
        if (_handle.IsAllocated) _handle.Free();
        _data = null;
        Unregister(this);
        GC.SuppressFinalize(this);
    }

    ~SecureBuffer() => Dispose();

    private static void Register(SecureBuffer b)
    {
        lock (RegistryLock) Registry.Add(b);
    }

    private static void Unregister(SecureBuffer b)
    {
        lock (RegistryLock) Registry.Remove(b);
    }

    /// <summary>Erases and disposes every live buffer (call on lock / app exit).</summary>
    public static void CloseAll()
    {
        SecureBuffer[] all;
        lock (RegistryLock) all = Registry.ToArray();
        foreach (var b in all) b.Dispose();
    }
}
