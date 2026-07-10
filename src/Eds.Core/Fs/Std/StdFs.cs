using Eds.Core.Fs;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;
using HostPath = System.IO.Path;
using HostFile = System.IO.File;
using HostDirectory = System.IO.Directory;

namespace Eds.Core.Fs.Std;

/// <summary>
/// Device filesystem over the host <see cref="System.IO"/> APIs. Managed port of
/// <c>fs.std.StdFs</c>. Rooted at a real host directory; virtual paths (always
/// '/'-separated) are resolved beneath that root. This is the "file/folder on
/// device" source that the locations layer (Phase D) mounts and that container
/// files live in.
///
/// Unlike the original (which had a whole-device root mode via an empty rootDir),
/// this port always takes an explicit host root directory, which is both the
/// common MAUI case and cross-platform-safe (no reliance on POSIX absolute
/// paths).
/// </summary>
public sealed class StdFs : IFileSystem
{
    private readonly string _rootDir;

    public StdFs(string rootDir)
    {
        if (string.IsNullOrEmpty(rootDir))
            throw new ArgumentException("Root directory must be provided", nameof(rootDir));
        _rootDir = HostPath.GetFullPath(rootDir);
    }

    public string RootDir => _rootDir;

    public IPath GetRootPath() => GetPath("/");

    public IPath GetPath(string pathString) => new StdFsPath(this, pathString);

    public void Close(bool force) { }
    public bool IsClosed => false;

    /// <summary>Maps a '/'-separated virtual path to the host OS path under the root.</summary>
    internal string ToHostPath(string virtualPath)
    {
        string p = _rootDir;
        foreach (var comp in StringPathUtil.SplitPath(virtualPath))
            p = HostPath.Combine(p, comp);
        return p;
    }
}

/// <summary>Path within a <see cref="StdFs"/>. Port of <c>StdFsPath</c>.</summary>
public sealed class StdFsPath : PathBase
{
    private readonly StdFs _stdFs;
    private readonly string _pathString;

    public StdFsPath(StdFs stdFs, string pathString) : base(stdFs)
    {
        _stdFs = stdFs;
        _pathString = pathString;
    }

    public override string PathString => _pathString;

    public string HostPathString => _stdFs.ToHostPath(_pathString);

    public override bool Exists() => HostFile.Exists(HostPathString) || HostDirectory.Exists(HostPathString);
    public override bool IsFile() => HostFile.Exists(HostPathString);
    public override bool IsDirectory() => HostDirectory.Exists(HostPathString);

    public override IDirectory GetDirectory() => new StdDirRecord(_stdFs, this);
    public override IFile GetFile() => new StdFileRecord(this);
}

/// <summary>Common file/directory record over the host FS. Port of <c>StdFsRecord</c>.</summary>
public abstract class StdFsRecord : IFsRecord
{
    protected StdFsPath PathRef;

    protected StdFsRecord(StdFsPath path) => PathRef = path;

    public IPath Path => PathRef;

    public string GetName() => PathRef.GetPathUtil().GetFileName();

    public DateTimeOffset GetLastModified()
        => new DateTimeOffset(HostFile.GetLastWriteTimeUtc(PathRef.HostPathString), TimeSpan.Zero);

    public void SetLastModified(DateTimeOffset dt)
    {
        if (IsDir(PathRef.HostPathString))
            HostDirectory.SetLastWriteTimeUtc(PathRef.HostPathString, dt.UtcDateTime);
        else
            HostFile.SetLastWriteTimeUtc(PathRef.HostPathString, dt.UtcDateTime);
    }

    public virtual void Delete()
    {
        string p = PathRef.HostPathString;
        if (HostFile.Exists(p)) HostFile.Delete(p);
    }

    public void Rename(string newName)
    {
        var parent = PathRef.GetParentPath() ?? PathRef.FileSystem.GetRootPath();
        MoveToPath((StdFsPath)parent.Combine(newName));
    }

    public void MoveTo(IDirectory newParent)
        => MoveToPath((StdFsPath)newParent.Path.Combine(GetName()));

    protected void MoveToPath(StdFsPath dest)
    {
        string src = PathRef.HostPathString;
        string dst = dest.HostPathString;
        if (IsDir(src)) HostDirectory.Move(src, dst);
        else HostFile.Move(src, dst);
        PathRef = dest;
    }

    protected static bool IsDir(string hostPath) => HostDirectory.Exists(hostPath);
}

/// <summary>Directory record. Port of <c>StdDirRecord</c>.</summary>
public sealed class StdDirRecord : StdFsRecord, IDirectory
{
    private readonly StdFs _stdFs;

    public StdDirRecord(StdFs stdFs, StdFsPath path) : base(path)
    {
        _stdFs = stdFs;
        if (path.Exists() && !path.IsDirectory())
            throw new ArgumentException("StdDirRecord: path must be a directory");
    }

    public long GetTotalSpace() => TryDrive(d => d.TotalSize);
    public long GetFreeSpace() => TryDrive(d => d.AvailableFreeSpace);

    private long TryDrive(Func<DriveInfo, long> pick)
    {
        try
        {
            string? root = HostPath.GetPathRoot(PathRef.HostPathString);
            if (string.IsNullOrEmpty(root)) return 0;
            return pick(new DriveInfo(root));
        }
        catch { return 0; }
    }

    public override void Delete()
    {
        string p = PathRef.HostPathString;
        if (HostDirectory.Exists(p))
        {
            if (HostDirectory.EnumerateFileSystemEntries(p).Any())
                throw new IOException("Directory is not empty: " + PathRef.PathDesc);
            HostDirectory.Delete(p);
        }
    }

    public IDirectory CreateDirectory(string name)
    {
        var newPath = (StdFsPath)PathRef.Combine(name);
        HostDirectory.CreateDirectory(newPath.HostPathString);
        return new StdDirRecord(_stdFs, newPath);
    }

    public IFile CreateFile(string name)
    {
        var newPath = (StdFsPath)PathRef.Combine(name);
        using (HostFile.Create(newPath.HostPathString)) { }
        return new StdFileRecord(newPath);
    }

    public IDirectoryContents List()
    {
        var res = new List<IPath>();
        string p = PathRef.HostPathString;
        if (HostDirectory.Exists(p))
            foreach (var entry in HostDirectory.EnumerateFileSystemEntries(p))
            {
                string name = HostPath.GetFileName(entry.TrimEnd(HostPath.DirectorySeparatorChar,
                                                                  HostPath.AltDirectorySeparatorChar));
                res.Add(PathRef.Combine(name));
            }
        return new ListContents(res);
    }

    private sealed class ListContents(List<IPath> items) : IDirectoryContents
    {
        public IEnumerator<IPath> GetEnumerator() => items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { }
    }
}

/// <summary>File record. Port of <c>StdFileRecord</c>.</summary>
public sealed class StdFileRecord : StdFsRecord, IFile
{
    public StdFileRecord(StdFsPath path) : base(path)
    {
        if (path.Exists() && !path.IsFile())
            throw new ArgumentException("StdFileRecord: path must be a file");
    }

    public long GetSize() => new FileInfo(PathRef.HostPathString).Length;

    public Stream GetInputStream()
        => new FileStream(PathRef.HostPathString, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream GetOutputStream()
        => new FileStream(PathRef.HostPathString, FileMode.Create, FileAccess.Write, FileShare.Read);

    public IRandomAccessIO GetRandomAccessIO(FileAccessMode accessMode)
        => new StdFsFileIO(PathRef.HostPathString, accessMode);

    public void CopyToOutputStream(Stream output, long offset, long count, IFileProgressInfo? progress)
    {
        using var io = GetRandomAccessIO(FileAccessMode.Read);
        io.Seek(offset);
        Copy(new Util.RandomAccessInputStream(io, ownsIo: false), output, count, progress);
    }

    public void CopyFromInputStream(Stream input, long offset, long count, IFileProgressInfo? progress)
    {
        using var io = GetRandomAccessIO(FileAccessMode.ReadWrite);
        io.Seek(offset);
        using var output = new Util.RandomAccessOutputStream(io, ownsIo: false);
        Copy(input, output, count, progress);
    }

    private static void Copy(Stream input, Stream output, long count, IFileProgressInfo? progress)
    {
        var buf = new byte[64 * 1024];
        long done = 0;
        while (done < count)
        {
            if (progress?.IsCancelled == true) break;
            int want = (int)Math.Min(buf.Length, count - done);
            int n = input.Read(buf, 0, want);
            if (n <= 0) break;
            output.Write(buf, 0, n);
            done += n;
            progress?.SetProcessed(done);
        }
        output.Flush();
    }
}
