namespace Eds.Core.Fs.Fat;

/// <summary>
/// Read-only <see cref="IRandomAccessIO"/> over a FAT file's cluster chain.
/// Maps a virtual file offset to (cluster, offset-in-cluster) and reads from the
/// underlying volume, bounded by the file size. Mirrors the read half of
/// <c>fs.fat.FileIO</c>.
/// </summary>
public sealed class FatFileReader : IRandomAccessIO
{
    private readonly IRandomAccessIO _io;
    private readonly Bpb _bpb;
    private readonly List<long> _clusters;
    private readonly long _size;
    private long _position;

    public FatFileReader(IRandomAccessIO io, Bpb bpb, List<long> clusters, long size)
    {
        _io = io;
        _bpb = bpb;
        _clusters = clusters;
        _size = size;
    }

    public void Seek(long position) => _position = position;
    public long GetFilePointer() => _position;
    public long Length() => _size;

    public int ReadByte()
    {
        var b = new byte[1];
        return Read(b, 0, 1) <= 0 ? -1 : b[0];
    }

    public int Read(byte[] buffer, int offset, int len)
    {
        if (_position >= _size) return -1;
        if (_position + len > _size) len = (int)(_size - _position);
        if (len <= 0) return 0;

        int clusterSize = _bpb.ClusterSize;
        int done = 0;
        while (done < len)
        {
            long clusterIndex = _position / clusterSize;
            int within = (int)(_position % clusterSize);
            if (clusterIndex >= _clusters.Count) break;

            long cluster = _clusters[(int)clusterIndex];
            int chunk = Math.Min(len - done, clusterSize - within);

            _io.Seek(_bpb.GetClusterOffset(cluster) + within);
            int n = IoUtil.ReadBytes(_io, buffer, offset + done, chunk);
            if (n <= 0) break;
            done += n;
            _position += n;
            if (n < chunk) break;
        }
        return done == 0 ? -1 : done;
    }

    // Read-only stream for now (write support is a later increment).
    public void WriteByte(int b) => throw new NotSupportedException("FAT file writing not yet implemented");
    public void Write(byte[] buffer, int offset, int len) => throw new NotSupportedException("FAT file writing not yet implemented");
    public void Flush() { }
    public void SetLength(long newLength) => throw new NotSupportedException("FAT file resizing not yet implemented");

    public void Dispose() { /* does not own the underlying volume IO */ }
}
