namespace Eds.Core.Fs.Util;

/// <summary>
/// Immutable, case-insensitive path helper. Faithful port of
/// <c>fs.util.StringPathUtil</c>. The virtual filesystems always use '/' as the
/// separator (independent of the host OS), so it is hardcoded here.
///
/// Comparison/equality/hashing are case-insensitive, matching the original
/// (which used <c>equalsIgnoreCase</c> / <c>toLowerCase().hashCode()</c>). This
/// matters for FAT (case-insensitive) and for path de-duplication.
/// </summary>
public sealed class StringPathUtil : IComparable<StringPathUtil>, IEquatable<StringPathUtil>
{
    public const char SeparatorChar = '/';
    public const string Separator = "/";

    private readonly List<string> _components;

    public StringPathUtil() => _components = new List<string>();

    public StringPathUtil(string? pathString) => _components = SplitPath(pathString);

    public StringPathUtil(params string[] components) => _components = new List<string>(components);

    public StringPathUtil(IEnumerable<string> components) => _components = new List<string>(components);

    public StringPathUtil(StringPathUtil p1, IEnumerable<string> components)
    {
        _components = new List<string>(p1._components);
        _components.AddRange(components);
    }

    public StringPathUtil(StringPathUtil p1, params string[] components)
        : this(p1, (IEnumerable<string>)components) { }

    public StringPathUtil(string part, StringPathUtil parts)
    {
        _components = new List<string>(parts._components);
        _components.Insert(0, part);
    }

    public StringPathUtil(StringPathUtil p1, StringPathUtil p2) : this(p1, p2._components) { }

    // ---- static helpers ------------------------------------------------

    public static List<string> SplitPath(string? path)
    {
        var res = new List<string>();
        if (path != null)
            foreach (var s in path.Split(SeparatorChar))
                if (!string.IsNullOrWhiteSpace(s))
                    res.Add(s);
        return res;
    }

    public static string JoinPath(params string[] components) => JoinPath(components, 0, components.Length);

    public static string JoinPath(IReadOnlyList<string> components, int off, int count)
    {
        if (count == 0) return Separator;
        var sb = new System.Text.StringBuilder(Separator);
        for (int i = 0; i < count; i++)
        {
            sb.Append(components[i + off]);
            sb.Append(SeparatorChar);
        }
        sb.Remove(sb.Length - 1, 1);
        return sb.ToString();
    }

    public static string GetSubPath(string srcPath, int numToRemove)
    {
        var components = SplitPath(srcPath);
        if (components.Count < numToRemove + 1) return "";
        return JoinPath(components, numToRemove, components.Count - numToRemove);
    }

    public static string GetSubPath(string srcPath, string parentPath)
        => GetSubPath(srcPath, SplitPath(parentPath).Count);

    public static string GetFileNameWithoutExtension(string fn)
    {
        int dotIndex = fn.LastIndexOf('.');
        return dotIndex > 0 ? fn.Substring(0, dotIndex) : fn;
    }

    public static string GetFileExtension(string fn)
    {
        int dotIndex = fn.LastIndexOf('.');
        return dotIndex > 0 ? fn.Substring(dotIndex + 1) : "";
    }

    // ---- instance ------------------------------------------------------

    public StringPathUtil Combine(string part) => Combine(new StringPathUtil(part));
    public StringPathUtil Combine(StringPathUtil part) => new(this, part);

    public bool IsEmpty => _components.Count == 0;

    public bool IsSpecial
    {
        get { var n = GetFileName(); return n == "." || n == ".."; }
    }

    public string[] GetComponents() => _components.ToArray();
    public int NumComponents => _components.Count;

    public StringPathUtil GetParentPath()
    {
        if (_components.Count < 2) return new StringPathUtil();
        return new StringPathUtil(_components.GetRange(0, _components.Count - 1));
    }

    public StringPathUtil GetSubPath(int numToRemove)
    {
        if (_components.Count < numToRemove + 1) return new StringPathUtil();
        return new StringPathUtil(_components.GetRange(numToRemove, _components.Count - numToRemove));
    }

    public StringPathUtil GetSubPath(StringPathUtil parentPath) => GetSubPath(parentPath._components.Count);

    public string GetFileName() => _components.Count > 0 ? _components[^1] : "";

    public string GetFileNameWithoutExtension() => GetFileNameWithoutExtension(GetFileName());
    public string GetFileExtension() => GetFileExtension(GetFileName());

    public bool IsParentDir(StringPathUtil subPath)
    {
        int s = _components.Count;
        if (subPath._components.Count <= s) return false;
        for (int i = 0; i < s; i++)
            if (!string.Equals(_components[i], subPath._components[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    public override string ToString() => JoinPath(_components, 0, _components.Count);

    public int CompareTo(StringPathUtil? other)
        => string.CompareOrdinal(ToString(), other?.ToString());

    public bool Equals(StringPathUtil? other)
    {
        if (other is null) return false;
        if (other._components.Count != _components.Count) return false;
        for (int i = 0; i < _components.Count; i++)
            if (!string.Equals(_components[i], other._components[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    public override bool Equals(object? o)
    {
        if (o is StringPathUtil spu) return Equals(spu);
        if (o is string s) return Equals(new StringPathUtil(s));
        return false;
    }

    public override int GetHashCode()
    {
        int res = 0;
        foreach (var c in _components)
            res ^= c.ToLowerInvariant().GetHashCode();
        return res;
    }
}
