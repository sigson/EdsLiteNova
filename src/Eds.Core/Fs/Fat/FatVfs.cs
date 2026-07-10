using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.Fat;

/// <summary>
/// Adapts the FAT core (<see cref="FatFileSystem"/>) to the unified
/// <see cref="Eds.Core.Fs.Vfs.IFileSystem"/> contract (the same model StdFs and
/// EncFS implement), so a FAT volume — whether a device volume or the inner
/// filesystem of an opened container — can be mounted and navigated uniformly by
/// the locations layer. This supersedes the early minimal
/// <c>Eds.Core.Fs.Abstract</c> adapter for library use.
///
/// The FAT core exposes whole-file writes (<c>WriteFile</c>), so writable
/// random-access is provided by a write-back buffer that rewrites the file on
/// flush (delete-then-write, since <c>WriteFile</c> appends a directory entry
/// rather than overwriting).
/// </summary>
public sealed class FatVfs : IFileSystem
{
    private readonly FatFileSystem _core;
    private readonly bool _writable;

    public FatVfs(FatFileSystem core, bool writable = true)
    {
        _core = core;
        _writable = writable;
    }

    internal FatFileSystem Core => _core;
    internal bool Writable => _writable;
    internal void EnsureWritable()
    {
        if (!_writable) throw new NotSupportedException("FAT volume is mounted read-only");
    }

    public IPath GetRootPath() => new FatPath(this, "/");
    public IPath GetPath(string pathString) => new FatPath(this, "/" + string.Join('/', new StringPathUtil(pathString).GetComponents()));

    public void Close(bool force) { }
    public bool IsClosed => false;
}

/// <summary>A path in a <see cref="FatVfs"/>. The string form is the FAT path.</summary>
public sealed class FatPath : PathBase
{
    private readonly FatVfs _fs;
    private readonly string _path;

    public FatPath(FatVfs fs, string path) : base(fs)
    {
        _fs = fs;
        _path = path;
    }

    internal FatVfs Fs => _fs;
    public override string PathString => _path;

    internal bool IsRoot => new StringPathUtil(_path).IsEmpty;
    internal FatDirEntry? Resolve() => IsRoot ? null : _fs.Core.ResolvePath(_path);

    public override bool Exists() => IsRoot || Resolve() != null;
    public override bool IsFile() { var e = Resolve(); return e != null && !e.IsDirectory; }
    public override bool IsDirectory() => IsRoot || (Resolve()?.IsDirectory ?? false);

    public override IDirectory GetDirectory() => new FatVfsDirectory(this);
    public override IFile GetFile() => new FatVfsFile(this);
}

internal abstract class FatVfsRecord : IFsRecord
{
    protected FatPath P;
    protected FatVfsRecord(FatPath p) => P = p;

    public IPath Path => P;
    public string GetName() => new StringPathUtil(P.PathString).GetFileName();

    // FAT timestamps are not surfaced by the core; report a stable epoch.
    public DateTimeOffset GetLastModified() => DateTimeOffset.UnixEpoch;
    public void SetLastModified(DateTimeOffset dt) { }

    public void Rename(string newName)
    {
        P.Fs.EnsureWritable();
        P.Fs.Core.Rename(P.PathString, newName);
        var parent = new StringPathUtil(P.PathString).GetParentPath();
        P = new FatPath(P.Fs, "/" + string.Join('/', parent.Combine(newName).GetComponents()));
    }

    public void Delete()
    {
        P.Fs.EnsureWritable();
        P.Fs.Core.Delete(P.PathString);
    }

    public void MoveTo(IDirectory newParent)
    {
        P.Fs.EnsureWritable();
        string dest = newParent.Path.Combine(GetName()).PathString;
        P.Fs.Core.Move(P.PathString, dest);
        P = new FatPath(P.Fs, dest);
    }
}

internal sealed class FatVfsDirectory : FatVfsRecord, IDirectory
{
    public FatVfsDirectory(FatPath p) : base(p) { }

    public long GetTotalSpace() => P.Fs.Core.TotalSize;
    public long GetFreeSpace() => 0; // FAT core does not surface free space

    public IDirectory CreateDirectory(string name)
    {
        P.Fs.EnsureWritable();
        string child = ChildPath(name);
        P.Fs.Core.CreateDirectory(child);
        return new FatVfsDirectory((FatPath)P.Fs.GetPath(child));
    }

    public IFile CreateFile(string name)
    {
        P.Fs.EnsureWritable();
        string child = ChildPath(name);
        P.Fs.Core.WriteFile(child, Array.Empty<byte>());
        return new FatVfsFile((FatPath)P.Fs.GetPath(child));
    }

    public IDirectoryContents List()
    {
        FatDirEntry? entry = P.IsRoot ? null : P.Resolve();
        IReadOnlyList<FatDirEntry> entries = entry == null ? P.Fs.Core.ListRoot() : P.Fs.Core.ListDirectory(entry);
        var res = new List<IPath>();
        foreach (var e in entries)
        {
            if (e.Name is "." or "..") continue;
            if (e.IsVolumeLabel) continue;
            res.Add(P.Fs.GetPath(ChildPath(e.Name)));
        }
        return new FatContents(res);
    }

    private string ChildPath(string name) => new StringPathUtil(P.PathString).Combine(name).ToString();

    private sealed class FatContents : IDirectoryContents
    {
        private readonly List<IPath> _items;
        public FatContents(List<IPath> items) => _items = items;
        public IEnumerator<IPath> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { }
    }
}

internal sealed class FatVfsFile : FatVfsRecord, IFile
{
    public FatVfsFile(FatPath p) : base(p) { }

    public long GetSize() => P.Resolve()?.Size ?? 0;

    public Stream GetInputStream()
        => new RandomAccessInputStream(GetRandomAccessIO(FileAccessMode.Read), ownsIo: true);

    public Stream GetOutputStream()
        => new RandomAccessOutputStream(GetRandomAccessIO(FileAccessMode.ReadWriteTruncate), ownsIo: true);

    public IRandomAccessIO GetRandomAccessIO(FileAccessMode accessMode)
    {
        if (accessMode == FileAccessMode.Read)
        {
            var e = P.Resolve() ?? throw new FileNotFoundException(P.PathString);
            return P.Fs.Core.OpenFile(e);
        }

        P.Fs.EnsureWritable();
        byte[] initial = Array.Empty<byte>();
        if (accessMode is FileAccessMode.ReadWrite or FileAccessMode.WriteAppend)
        {
            var e = P.Resolve();
            if (e != null && !e.IsDirectory) initial = P.Fs.Core.ReadAllBytes(e);
        }
        var io = new FatWriteBackIO(P.Fs.Core, P.PathString, initial);
        if (accessMode == FileAccessMode.WriteAppend) io.Seek(io.Length());
        return io;
    }

    public void CopyToOutputStream(Stream output, long offset, long count, IFileProgressInfo? progress)
    {
        using var io = GetRandomAccessIO(FileAccessMode.Read);
        io.Seek(offset);
        using var ins = new RandomAccessInputStream(io, ownsIo: false);
        WrapperCopy.Copy(ins, output, count, progress);
    }

    public void CopyFromInputStream(Stream input, long offset, long count, IFileProgressInfo? progress)
    {
        using var io = GetRandomAccessIO(FileAccessMode.ReadWrite);
        io.Seek(offset);
        using var os = new RandomAccessOutputStream(io, ownsIo: false);
        WrapperCopy.Copy(input, os, count, progress);
    }
}

/// <summary>
/// Writable random-access over a FAT file backed by an in-memory buffer that is
/// written back on flush/close. The FAT core only supports whole-file writes and
/// appends a directory entry (no in-place overwrite), so flushing deletes any
/// existing entry first, then writes the buffer.
/// </summary>
internal sealed class FatWriteBackIO : IRandomAccessIO
{
    private readonly FatFileSystem _core;
    private readonly string _path;
    private byte[] _data;
    private long _length;
    private long _pos;
    private bool _dirty;

    public FatWriteBackIO(FatFileSystem core, string path, byte[] initial)
    {
        _core = core;
        _path = path;
        _data = (byte[])initial.Clone();
        _length = initial.Length;
    }

    public void Seek(long position) => _pos = position;
    public long GetFilePointer() => _pos;
    public long Length() => _length;

    public int ReadByte()
    {
        if (_pos >= _length) return -1;
        return _data[_pos++];
    }

    public int Read(byte[] buffer, int offset, int len)
    {
        if (_pos >= _length) return -1;
        int n = (int)Math.Min(len, _length - _pos);
        Array.Copy(_data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public void WriteByte(int b) => Write(new[] { (byte)b }, 0, 1);

    public void Write(byte[] buffer, int offset, int len)
    {
        EnsureCapacity(_pos + len);
        Array.Copy(buffer, offset, _data, _pos, len);
        _pos += len;
        if (_pos > _length) _length = _pos;
        _dirty = true;
    }

    public void SetLength(long newLength)
    {
        EnsureCapacity(newLength);
        if (newLength < _length) Array.Clear(_data, (int)newLength, (int)(_length - newLength));
        _length = newLength;
        if (_pos > _length) _pos = _length;
        _dirty = true;
    }

    public void Flush()
    {
        if (!_dirty) return;
        var content = new byte[_length];
        Array.Copy(_data, content, (int)_length);
        if (_core.ResolvePath(_path) != null) _core.Delete(_path);
        _core.WriteFile(_path, content);
        _dirty = false;
    }

    private void EnsureCapacity(long need)
    {
        if (need <= _data.Length) return;
        long newCap = Math.Max(need, Math.Max(64, _data.Length * 2L));
        var grown = new byte[newCap];
        Array.Copy(_data, grown, _data.Length);
        _data = grown;
    }

    public void Dispose() => Flush();
}
