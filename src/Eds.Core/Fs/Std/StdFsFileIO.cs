using Eds.Core.Fs;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.Std;

/// <summary>
/// <see cref="IRandomAccessIO"/> over a host <see cref="FileStream"/>. Managed
/// replacement for <c>fs.std.StdFsFileIO</c> (which subclassed Java's
/// <c>RandomAccessFile</c>). The access-mode mapping matches the original
/// ("r" vs "rw", plus truncate/append handling), and <see cref="Dispose"/>
/// fsyncs like the original's <c>close()</c>.
/// </summary>
public sealed class StdFsFileIO : IRandomAccessIO
{
    private readonly FileStream _fs;

    public StdFsFileIO(string hostPath, FileAccessMode mode)
    {
        bool readOnly = mode == FileAccessMode.Read;
        _fs = new FileStream(
            hostPath,
            readOnly ? FileMode.Open : FileMode.OpenOrCreate,
            readOnly ? FileAccess.Read : FileAccess.ReadWrite,
            FileShare.Read);

        if (mode == FileAccessMode.ReadWriteTruncate)
            _fs.SetLength(0);
        else if (mode == FileAccessMode.WriteAppend)
            _fs.Seek(0, SeekOrigin.End);
    }

    public void Seek(long position) => _fs.Position = position;
    public long GetFilePointer() => _fs.Position;
    public long Length() => _fs.Length;

    public int ReadByte() => _fs.ReadByte();

    public int Read(byte[] buffer, int offset, int len)
    {
        int n = _fs.Read(buffer, offset, len);
        return n == 0 && len > 0 ? -1 : n;
    }

    public void WriteByte(int b) => _fs.WriteByte((byte)b);
    public void Write(byte[] buffer, int offset, int len) => _fs.Write(buffer, offset, len);
    public void Flush() => _fs.Flush(true);
    public void SetLength(long newLength) => _fs.SetLength(newLength);

    public void Dispose()
    {
        try { _fs.Flush(true); } catch { /* best-effort fsync */ }
        _fs.Dispose();
    }
}
