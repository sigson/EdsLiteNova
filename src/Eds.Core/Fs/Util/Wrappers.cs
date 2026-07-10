using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.Util;

/// <summary>
/// Decorator base for a file/directory record. Port of <c>fs.util.FSRecordWrapper</c>.
/// Wraps a base <see cref="IFsRecord"/> and re-maps the record's path through
/// <see cref="GetPathFromBasePath"/> after operations that change it (rename/move).
/// Subclasses (e.g. EncFS) override specific members to transform names/contents.
/// </summary>
public abstract class FsRecordWrapper : IFsRecord
{
    private readonly IFsRecord _base;
    private IPath _path;

    protected FsRecordWrapper(IPath path, IFsRecord baseRecord)
    {
        _base = baseRecord;
        _path = path;
    }

    public IPath Path => _path;

    public virtual string GetName() => _base.GetName();

    public virtual void Rename(string newName)
    {
        _base.Rename(newName);
        SetPath(GetPathFromBasePath(_base.Path));
    }

    public virtual DateTimeOffset GetLastModified() => _base.GetLastModified();
    public virtual void SetLastModified(DateTimeOffset dt) => _base.SetLastModified(dt);
    public virtual void Delete() => _base.Delete();

    public virtual void MoveTo(IDirectory newParent)
    {
        _base.MoveTo(((DirectoryWrapper)newParent).GetBase());
        SetPath(GetPathFromBasePath(_base.Path));
    }

    public IFsRecord GetBase() => _base;

    protected abstract IPath GetPathFromBasePath(IPath basePath);
    protected void SetPath(IPath path) => _path = path;
}

/// <summary>Decorator base for a file. Port of <c>fs.util.FileWrapper</c>.</summary>
public abstract class FileWrapper : FsRecordWrapper, IFile
{
    protected FileWrapper(IPath path, IFile baseFile) : base(path, baseFile) { }

    public new IFile GetBase() => (IFile)base.GetBase();

    public virtual Stream GetInputStream() => GetBase().GetInputStream();
    public virtual Stream GetOutputStream() => GetBase().GetOutputStream();
    public virtual IRandomAccessIO GetRandomAccessIO(FileAccessMode accessMode) => GetBase().GetRandomAccessIO(accessMode);
    public virtual long GetSize() => GetBase().GetSize();

    public virtual void CopyToOutputStream(Stream output, long offset, long count, IFileProgressInfo? progress)
    {
        using var io = GetRandomAccessIO(FileAccessMode.Read);
        io.Seek(offset);
        using var ins = new RandomAccessInputStream(io, ownsIo: false);
        WrapperCopy.Copy(ins, output, count, progress);
    }

    public virtual void CopyFromInputStream(Stream input, long offset, long count, IFileProgressInfo? progress)
    {
        using var io = GetRandomAccessIO(FileAccessMode.ReadWrite);
        io.Seek(offset);
        using var os = new RandomAccessOutputStream(io, ownsIo: false);
        WrapperCopy.Copy(input, os, count, progress);
    }
}

/// <summary>Decorator base for a directory. Port of <c>fs.util.DirectoryWrapper</c>.</summary>
public abstract class DirectoryWrapper : FsRecordWrapper, IDirectory
{
    protected DirectoryWrapper(IPath path, IDirectory baseDir) : base(path, baseDir) { }

    public new IDirectory GetBase() => (IDirectory)base.GetBase();

    public virtual long GetTotalSpace() => GetBase().GetTotalSpace();
    public virtual long GetFreeSpace() => GetBase().GetFreeSpace();

    public virtual IDirectory CreateDirectory(string name)
    {
        IPath basePath = GetBase().CreateDirectory(name).Path;
        return GetPathFromBasePath(basePath).GetDirectory();
    }

    public virtual IFile CreateFile(string name)
    {
        IPath basePath = GetBase().CreateFile(name).Path;
        return GetPathFromBasePath(basePath).GetFile();
    }

    public virtual IDirectoryContents List() => new ContentsWrapper(this, GetBase().List());

    /// <summary>Maps a base-fs path to this wrapper's path space. Exposed to nested contents.</summary>
    internal IPath MapBasePath(IPath basePath) => GetPathFromBasePath(basePath);

    protected sealed class ContentsWrapper : IDirectoryContents
    {
        private readonly DirectoryWrapper _owner;
        private readonly IDirectoryContents _base;

        public ContentsWrapper(DirectoryWrapper owner, IDirectoryContents baseContents)
        {
            _owner = owner;
            _base = baseContents;
        }

        public void Dispose() => _base.Dispose();

        public IEnumerator<IPath> GetEnumerator()
        {
            foreach (var src in _base)
            {
                IPath? mapped;
                try { mapped = _owner.MapBasePath(src); }
                catch { mapped = null; }
                if (mapped != null) yield return mapped;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>Decorator base for a filesystem. Port of <c>fs.util.FileSystemWrapper</c>.</summary>
public abstract class FileSystemWrapper : IFileSystem
{
    private readonly IFileSystem _base;

    protected FileSystemWrapper(IFileSystem baseFs) => _base = baseFs;

    public IFileSystem GetBase() => _base;

    public abstract IPath GetRootPath();
    public abstract IPath GetPath(string pathString);

    public virtual void Close(bool force) => _base.Close(force);
    public virtual bool IsClosed => _base.IsClosed;

    public override bool Equals(object? o)
        => o is FileSystemWrapper w ? _base.Equals(w.GetBase()) : _base.Equals(o);

    public override int GetHashCode() => _base.GetHashCode();
}

internal static class WrapperCopy
{
    public static void Copy(Stream input, Stream output, long count, IFileProgressInfo? progress)
    {
        long limit = count <= 0 ? long.MaxValue : count; // count<=0 means copy to EOF
        var buf = new byte[64 * 1024];
        long done = 0;
        while (done < limit)
        {
            if (progress?.IsCancelled == true) break;
            int want = (int)Math.Min(buf.Length, limit - done);
            int n = input.Read(buf, 0, want);
            if (n <= 0) break;
            output.Write(buf, 0, n);
            done += n;
            progress?.SetProcessed(done);
        }
        output.Flush();
    }
}
