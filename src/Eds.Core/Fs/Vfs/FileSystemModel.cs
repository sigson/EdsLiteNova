namespace Eds.Core.Fs.Vfs;

/// <summary>
/// Full path-oriented filesystem contract. Faithful port of the
/// <c>com.sovworks.eds.fs</c> interface set (FileSystem/Path/FSRecord/Directory/
/// File/FileSystemInfo). This is the canonical model the container filesystems
/// (FAT, EncFS) and the device filesystem (StdFs) implement, and the one the
/// locations layer builds on.
///
/// <para>It supersedes the minimal <c>Eds.Core.Fs.Abstract.IFileSystem</c> used
/// by the early FAT browser; both coexist during the transition (the FAT driver
/// will be re-adapted onto this contract). Java's <c>DataInput</c>/<c>DataOutput</c>
/// are already folded into <see cref="IRandomAccessIO"/>, and the Android-only
/// <c>ParcelFileDescriptor</c> accessor is intentionally omitted (SAF lives in
/// the Android target, see gap guide §9.6).</para>
/// </summary>
public interface IFileSystem
{
    /// <summary>Root path ("/"). Mirrors <c>getRootPath()</c>.</summary>
    IPath GetRootPath();

    /// <summary>Resolves a string path within this filesystem.</summary>
    IPath GetPath(string pathString);

    /// <summary>Closes the filesystem. <paramref name="force"/> ignores open handles.</summary>
    void Close(bool force);

    bool IsClosed { get; }
}

/// <summary>A path within a <see cref="IFileSystem"/>. Mirrors <c>fs.Path</c>.</summary>
public interface IPath : IComparable<IPath>
{
    IFileSystem FileSystem { get; }
    string PathString { get; }

    /// <summary>Human-readable description (defaults to the path string).</summary>
    string PathDesc { get; }

    bool Exists();
    bool IsFile();
    bool IsDirectory();
    bool IsRootDirectory();

    /// <summary>Appends a child component and resolves the new path.</summary>
    IPath Combine(string part);

    IDirectory GetDirectory();
    IFile GetFile();

    /// <summary>Parent path, or null at the root.</summary>
    IPath? GetParentPath();
}

/// <summary>Common members of files and directories. Mirrors <c>fs.FSRecord</c>.</summary>
public interface IFsRecord
{
    IPath Path { get; }
    string GetName();
    void Rename(string newName);
    DateTimeOffset GetLastModified();
    void SetLastModified(DateTimeOffset dt);
    void Delete();
    void MoveTo(IDirectory newParent);
}

/// <summary>A directory listing that must be disposed. Mirrors <c>Directory.Contents</c>.</summary>
public interface IDirectoryContents : IEnumerable<IPath>, IDisposable { }

/// <summary>A directory. Mirrors <c>fs.Directory</c>.</summary>
public interface IDirectory : IFsRecord
{
    IDirectory CreateDirectory(string name);
    IFile CreateFile(string name);
    IDirectoryContents List();
    long GetTotalSpace();
    long GetFreeSpace();
}

/// <summary>File open modes. Mirrors <c>File.AccessMode</c>.</summary>
public enum FileAccessMode
{
    Read,
    Write,
    WriteAppend,
    ReadWrite,
    ReadWriteTruncate
}

/// <summary>Progress + cancellation for long file copies. Mirrors <c>File.ProgressInfo</c>.</summary>
public interface IFileProgressInfo
{
    void SetProcessed(long num);
    bool IsCancelled { get; }
}

/// <summary>A file. Mirrors <c>fs.File</c> (minus the Android FD accessor).</summary>
public interface IFile : IFsRecord
{
    Stream GetInputStream();
    Stream GetOutputStream();
    IRandomAccessIO GetRandomAccessIO(FileAccessMode accessMode);
    long GetSize();
    void CopyToOutputStream(Stream output, long offset, long count, IFileProgressInfo? progress);
    void CopyFromInputStream(Stream input, long offset, long count, IFileProgressInfo? progress);
}

/// <summary>
/// Filesystem factory metadata. Mirrors <c>fs.FileSystemInfo</c> (Parcelable
/// dropped). Concrete infos (FAT, exFAT) create/open a filesystem over a raw
/// <see cref="IRandomAccessIO"/> image.
/// </summary>
public interface IFileSystemInfo
{
    string FileSystemName { get; }
    void MakeNewFileSystem(IRandomAccessIO img);
    IFileSystem OpenFileSystem(IRandomAccessIO img, bool readOnly);
}
