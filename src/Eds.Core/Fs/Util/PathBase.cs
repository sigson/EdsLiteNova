using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.Util;

/// <summary>
/// Base implementation of <see cref="IPath"/> that derives parent/combine/equality
/// from the string path via <see cref="StringPathUtil"/>. Faithful port of
/// <c>fs.util.PathBase</c>. Concrete filesystems subclass it and supply
/// <see cref="PathString"/>, <see cref="Exists"/>, <see cref="IsFile"/>,
/// <see cref="IsDirectory"/>, <see cref="GetDirectory"/> and <see cref="GetFile"/>.
/// </summary>
public abstract class PathBase : IPath, IEquatable<PathBase>
{
    private readonly IFileSystem _fs;

    protected PathBase(IFileSystem fs) => _fs = fs;

    public IFileSystem FileSystem => _fs;

    public abstract string PathString { get; }
    public virtual string PathDesc => PathString;

    public abstract bool Exists();
    public abstract bool IsFile();
    public abstract bool IsDirectory();
    public abstract IDirectory GetDirectory();
    public abstract IFile GetFile();

    public virtual bool IsRootDirectory() => IsDirectory() && GetParentPath() == null;

    public virtual IPath Combine(string part)
        => _fs.GetPath(GetPathUtil().Combine(part).ToString());

    public virtual IPath? GetParentPath()
    {
        var pu = GetPathUtil();
        return pu.IsEmpty ? null : _fs.GetPath(pu.GetParentPath().ToString());
    }

    public virtual StringPathUtil GetPathUtil() => new(PathString);

    public int CompareTo(IPath? other)
        => other == null ? 1 : GetPathUtil().CompareTo(new StringPathUtil(other.PathString));

    public bool Equals(PathBase? other)
        => other != null && GetPathUtil().Equals(other.GetPathUtil());

    public override bool Equals(object? o)
    {
        if (o is PathBase pb) return GetPathUtil().Equals(pb.GetPathUtil());
        if (o is string || o is StringPathUtil) return GetPathUtil().Equals(o);
        return false;
    }

    public override int GetHashCode() => GetPathUtil().GetHashCode();

    public override string ToString() => PathString;
}
