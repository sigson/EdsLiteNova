using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Services;

/// <summary>Field a directory listing is sorted by.</summary>
public enum FileSortField { Name, Size, LastModified, Type }

public enum SortDirection { Ascending, Descending }

/// <summary>
/// One materialised directory entry with the metadata the file manager sorts and
/// displays by, captured once so sorting doesn't re-hit the filesystem per compare.
/// </summary>
public sealed record FileListItem(
    IPath Path, string Name, bool IsDirectory, long Size, DateTimeOffset LastModified);

/// <summary>How to sort and filter a listing.</summary>
public sealed class FileListOptions
{
    public FileSortField Field { get; init; } = FileSortField.Name;
    public SortDirection Direction { get; init; } = SortDirection.Ascending;

    /// <summary>Keep directories grouped before files regardless of sort direction.</summary>
    public bool DirectoriesFirst { get; init; } = true;

    /// <summary>Optional predicate; entries for which it returns false are dropped.</summary>
    public Func<FileListItem, bool>? Filter { get; init; }

    public static readonly FileListOptions Default = new();
}

/// <summary>
/// Lists a directory into a sorted, filtered list of <see cref="FileListItem"/>.
/// Platform-independent port of the file manager's <c>comparators/</c> +
/// listing logic, over the Vfs abstraction. Directories-first grouping is kept
/// stable under both sort directions (as file managers conventionally do).
/// </summary>
public sealed class FileLister
{
    public IReadOnlyList<FileListItem> List(IDirectory directory, FileListOptions? options = null)
    {
        options ??= FileListOptions.Default;

        var items = new List<FileListItem>();
        using (var contents = directory.List())
        {
            foreach (var p in contents)
            {
                var item = Describe(p);
                if (options.Filter == null || options.Filter(item))
                    items.Add(item);
            }
        }

        items.Sort((a, b) => Compare(a, b, options));
        return items;
    }

    private static FileListItem Describe(IPath p)
    {
        bool isDir = p.IsDirectory();
        string name = new StringPathUtil(p.PathString).GetFileName();
        long size = 0;
        DateTimeOffset modified = default;

        try
        {
            if (isDir)
            {
                modified = p.GetDirectory().GetLastModified();
            }
            else
            {
                var f = p.GetFile();
                size = f.GetSize();
                modified = f.GetLastModified();
            }
        }
        catch
        {
            // Some backings may not expose size/mtime for every entry; leave defaults.
        }

        return new FileListItem(p, name, isDir, size, modified);
    }

    private static int Compare(FileListItem a, FileListItem b, FileListOptions o)
    {
        if (o.DirectoriesFirst && a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1; // dirs first, not affected by direction

        int cmp = o.Field switch
        {
            FileSortField.Size => a.Size.CompareTo(b.Size),
            FileSortField.LastModified => a.LastModified.CompareTo(b.LastModified),
            FileSortField.Type => CompareByType(a, b),
            _ => CompareByName(a, b),
        };

        // Stable tie-break on name so equal keys have a deterministic order.
        if (cmp == 0 && o.Field != FileSortField.Name) cmp = CompareByName(a, b);

        return o.Direction == SortDirection.Descending ? -cmp : cmp;
    }

    private static int CompareByName(FileListItem a, FileListItem b)
        => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static int CompareByType(FileListItem a, FileListItem b)
    {
        int c = string.Compare(Extension(a.Name), Extension(b.Name), StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : CompareByName(a, b);
    }

    private static string Extension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[(dot + 1)..];
    }
}
