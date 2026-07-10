namespace Eds.Core.Fs;

/// <summary>Seek/position/length. Mirrors <c>fs.RandomStorageAccess</c>.</summary>
public interface IRandomStorageAccess
{
    void Seek(long position);
    long GetFilePointer();
    long Length();
}

/// <summary>
/// Random-access byte IO. Mirrors <c>fs.RandomAccessIO</c>
/// (Closeable + RandomStorageAccess + DataInput + DataOutput + setLength).
/// </summary>
public interface IRandomAccessIO : IRandomStorageAccess, IDisposable
{
    /// <summary>Reads one byte (0..255) or -1 at end of stream.</summary>
    int ReadByte();
    /// <summary>Reads up to <paramref name="len"/> bytes; returns count or -1 at EOF.</summary>
    int Read(byte[] buffer, int offset, int len);
    void WriteByte(int b);
    void Write(byte[] buffer, int offset, int len);
    void Flush();
    void SetLength(long newLength);
}
