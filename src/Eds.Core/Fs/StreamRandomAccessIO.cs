namespace Eds.Core.Fs;

/// <summary>
/// <see cref="IRandomAccessIO"/> over a seekable <see cref="Stream"/> (the
/// managed replacement for <c>StdFsFileIO</c> / <c>FDRandomAccessIO</c>). For
/// plain files on desktop/Android this is all that's needed - the native
/// fdraio module is only required for Android SAF file descriptors.
/// </summary>
public sealed class StreamRandomAccessIO : IRandomAccessIO
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    public StreamRandomAccessIO(Stream stream, bool ownsStream = true)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable", nameof(stream));
        _stream = stream;
        _ownsStream = ownsStream;
    }

    /// <summary>Opens a file for random access.</summary>
    public static StreamRandomAccessIO OpenFile(string path, bool writable)
    {
        var fs = new FileStream(
            path,
            writable ? FileMode.OpenOrCreate : FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.Read);
        return new StreamRandomAccessIO(fs);
    }

    public void Seek(long position) => _stream.Position = position;
    public long GetFilePointer() => _stream.Position;
    public long Length() => _stream.Length;

    public int ReadByte() => _stream.ReadByte();

    public int Read(byte[] buffer, int offset, int len)
    {
        int n = _stream.Read(buffer, offset, len);
        return n == 0 && len > 0 ? -1 : n;
    }

    public void WriteByte(int b) => _stream.WriteByte((byte)b);
    public void Write(byte[] buffer, int offset, int len) => _stream.Write(buffer, offset, len);
    public void Flush() => _stream.Flush();
    public void SetLength(long newLength) => _stream.SetLength(newLength);

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}
