namespace Eds.Core.Fs.Abstract;

/// <summary>
/// Minimal, UI-friendly filesystem abstraction. Navigation is always available;
/// write operations (<see cref="IDirectory.CreateSubdirectory"/>,
/// <see cref="IDirectory.WriteFile"/>, <see cref="IFsObject.Delete"/>) are
/// supported when the underlying volume was mounted writable and the driver
/// implements them; otherwise they throw <see cref="NotSupportedException"/>.
/// </summary>
public interface IFileSystem
{
    string Name { get; }
    bool IsWritable { get; }
    IDirectory Root { get; }
}

/// <summary>Common members of files and directories.</summary>
public interface IFsObject
{
    string Name { get; }
    string FullPath { get; }
    bool IsDirectory { get; }
    void Delete();
    void Rename(string newName);
    void MoveTo(string destinationDirectoryPath);
}

public interface IDirectory : IFsObject
{
    IReadOnlyList<IFsObject> List();
    void CreateSubdirectory(string name);
    void WriteFile(string name, byte[] content);
}

public interface IFile : IFsObject
{
    long Size { get; }
    IRandomAccessIO Open();
    byte[] ReadAllBytes();
}
