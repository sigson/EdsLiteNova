using Eds.Core.Fs.Vfs;

namespace Eds.Core.Services;

/// <summary>
/// Immutable snapshot of a running file operation, delivered through
/// <see cref="IProgress{T}"/>. Replaces the Android <c>FilesOperationStatus</c>
/// (a Parcelable pushed via broadcast) with a plain value pushed via
/// <see cref="System.IProgress{T}"/>.
/// </summary>
public readonly record struct FileOperationStatus(
    string? CurrentFile,
    long FilesProcessed,
    long TotalFiles,
    long BytesProcessed,
    long TotalBytes)
{
    /// <summary>0..1 completion, by bytes when known, else by file count.</summary>
    public double FractionComplete =>
        TotalBytes > 0 ? Math.Clamp((double)BytesProcessed / TotalBytes, 0, 1)
        : TotalFiles > 0 ? Math.Clamp((double)FilesProcessed / TotalFiles, 0, 1)
        : 0;
}

/// <summary>
/// Aggregate count and byte size of a selection (files + recursive directory
/// contents). Port of <c>fs.util.FilesCountAndSize</c>; used to pre-compute the
/// totals a <see cref="FileOperationStatus"/> reports against.
/// </summary>
public readonly record struct FilesCountAndSize(long Count, long TotalSize)
{
    /// <summary>Walks the selection recursively, summing file count and bytes.</summary>
    public static FilesCountAndSize Scan(IEnumerable<IPath> paths, CancellationToken ct = default)
    {
        long count = 0, size = 0;
        void Visit(IPath p)
        {
            ct.ThrowIfCancellationRequested();
            if (p.IsFile())
            {
                count++;
                size += p.GetFile().GetSize();
            }
            else if (p.IsDirectory())
            {
                using var contents = p.GetDirectory().List();
                foreach (var child in contents) Visit(child);
            }
        }
        foreach (var p in paths) Visit(p);
        return new FilesCountAndSize(count, size);
    }
}
