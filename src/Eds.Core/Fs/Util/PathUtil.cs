using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.Util;

/// <summary>Path helpers. Partial port of <c>fs.util.PathUtil</c>.</summary>
public static class PathUtil
{
    /// <summary>
    /// Returns the last-component name of a path, resolving through the record
    /// when possible (a file/directory may report a transformed name, e.g. EncFS
    /// decodes it), else falling back to the string path util.
    /// </summary>
    public static string GetNameFromPath(IPath path)
    {
        if (path.IsFile()) return path.GetFile().GetName();
        if (path.IsDirectory()) return path.GetDirectory().GetName();
        return path is PathBase pb ? pb.GetPathUtil().GetFileName() : path.PathDesc;
    }
}
