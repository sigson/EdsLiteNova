using Eds.Core.Fs.Abstract;

namespace Eds.Core.Fs.Fat;

/// <summary>
/// Adapts <see cref="FatFileSystem"/> to the generic <see cref="IFileSystem"/>
/// abstraction. Directories and files carry their full path so the path-based
/// FAT write API (WriteFile/CreateDirectory/Delete) can be reached from the UI.
/// </summary>
public sealed class FatFileSystemAdapter : IFileSystem
{
    private readonly FatFileSystem _fs;

    public FatFileSystemAdapter(FatFileSystem fs, bool writable = false)
    {
        _fs = fs;
        IsWritable = writable;
    }

    public string Name => $"FAT ({_fs.Type})";
    public bool IsWritable { get; }
    public IDirectory Root => new FatDirectory(_fs, this, "/", null);

    private static string Combine(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    private void EnsureWritable()
    {
        if (!IsWritable) throw new NotSupportedException("Filesystem is mounted read-only");
    }

    private sealed class FatDirectory : IDirectory
    {
        private readonly FatFileSystem _fs;
        private readonly FatFileSystemAdapter _owner;
        private readonly FatDirEntry? _entry;

        public FatDirectory(FatFileSystem fs, FatFileSystemAdapter owner, string fullPath, FatDirEntry? entry)
        {
            _fs = fs;
            _owner = owner;
            FullPath = fullPath;
            _entry = entry;
        }

        public string Name => _entry?.Name ?? "/";
        public string FullPath { get; }
        public bool IsDirectory => true;

        public IReadOnlyList<IFsObject> List()
        {
            var entries = _entry == null ? _fs.ListRoot() : _fs.ListDirectory(_entry);
            var result = new List<IFsObject>(entries.Count);
            foreach (var e in entries)
            {
                if (e.Name is "." or "..") continue;
                string childPath = Combine(FullPath, e.Name);
                result.Add(e.IsDirectory
                    ? new FatDirectory(_fs, _owner, childPath, e)
                    : new FatFile(_fs, _owner, childPath, e));
            }
            return result;
        }

        public void CreateSubdirectory(string name)
        {
            _owner.EnsureWritable();
            _fs.CreateDirectory(Combine(FullPath, name));
        }

        public void WriteFile(string name, byte[] content)
        {
            _owner.EnsureWritable();
            _fs.WriteFile(Combine(FullPath, name), content);
        }

        public void Delete()
        {
            _owner.EnsureWritable();
            _fs.Delete(FullPath);
        }

        public void Rename(string newName)
        {
            _owner.EnsureWritable();
            _fs.Rename(FullPath, newName);
        }

        public void MoveTo(string destinationDirectoryPath)
        {
            _owner.EnsureWritable();
            _fs.Move(FullPath, Combine(destinationDirectoryPath, Name));
        }
    }

    private sealed class FatFile : IFile
    {
        private readonly FatFileSystem _fs;
        private readonly FatFileSystemAdapter _owner;
        private readonly FatDirEntry _entry;

        public FatFile(FatFileSystem fs, FatFileSystemAdapter owner, string fullPath, FatDirEntry entry)
        {
            _fs = fs;
            _owner = owner;
            FullPath = fullPath;
            _entry = entry;
        }

        public string Name => _entry.Name;
        public string FullPath { get; }
        public bool IsDirectory => false;
        public long Size => _entry.Size;
        public IRandomAccessIO Open() => _fs.OpenFile(_entry);
        public byte[] ReadAllBytes() => _fs.ReadAllBytes(_entry);

        public void Delete()
        {
            _owner.EnsureWritable();
            _fs.Delete(FullPath);
        }

        public void Rename(string newName)
        {
            _owner.EnsureWritable();
            _fs.Rename(FullPath, newName);
        }

        public void MoveTo(string destinationDirectoryPath)
        {
            _owner.EnsureWritable();
            _fs.Move(FullPath, Combine(destinationDirectoryPath, Name));
        }
    }
}
