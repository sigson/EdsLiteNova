using Eds.Core.Fs.Vfs;

namespace Eds.Core.Services;

/// <summary>What to do when a copy/move target already exists.</summary>
public enum OverwriteAction { Overwrite, Skip, Abort }

/// <summary>
/// Recursive file operations (copy / move / delete / wipe) over the
/// <see cref="IFileSystem"/> abstraction, with cancellation and progress. This is
/// the platform-independent heart of the Android <c>service/</c> tasks
/// (<c>CopyFilesTask</c>/<c>MoveFilesTask</c>/<c>DeleteFilesTask</c>/
/// <c>WipeFilesTask</c>), lifted out of the <c>IntentService</c> machinery: no
/// Intents, notifications or Parcelables — just synchronous methods driven by a
/// <see cref="CancellationToken"/> and an <see cref="IProgress{T}"/>. The
/// background/queue/notification concerns live in
/// <see cref="FileOperationsService"/> and the platform layer.
///
/// <para>Cross-filesystem moves fall back to copy-then-delete; same-filesystem
/// moves use the fast <see cref="IFsRecord.MoveTo"/>. Wipe overwrites file data
/// before deletion (best-effort on copy-on-write backings such as the FAT
/// write-back driver — see <see cref="WipeUtil"/>).</para>
/// </summary>
public sealed class FileOperations
{
    private readonly IProgress<FileOperationStatus>? _progress;
    private readonly CancellationToken _token;
    private readonly Func<IPath, OverwriteAction>? _onConflict;
    private readonly int _wipePasses;

    private long _totalFiles, _totalBytes, _filesDone, _bytesDone, _bytesBeforeCurrent;
    private string? _currentFile;

    public FileOperations(
        IProgress<FileOperationStatus>? progress = null,
        CancellationToken token = default,
        Func<IPath, OverwriteAction>? onConflict = null,
        int wipePasses = 1)
    {
        _progress = progress;
        _token = token;
        _onConflict = onConflict;
        _wipePasses = Math.Max(1, wipePasses);
    }

    // ---- public operations --------------------------------------------

    public void Copy(IReadOnlyList<IPath> sources, IDirectory destination)
    {
        Prescan(sources);
        foreach (var src in sources)
        {
            _token.ThrowIfCancellationRequested();
            CopyRecursive(src, destination);
        }
    }

    public void Move(IReadOnlyList<IPath> sources, IDirectory destination)
    {
        // Same-filesystem moves relocate entries without streaming bytes, so track
        // progress by file count (bytes total left at 0 → file-based fraction).
        _totalFiles = FilesCountAndSize.Scan(sources, _token).Count;
        _totalBytes = 0;
        foreach (var src in sources)
        {
            _token.ThrowIfCancellationRequested();
            MoveOne(src, destination);
        }
    }

    public void Delete(IReadOnlyList<IPath> sources)
    {
        _totalFiles = FilesCountAndSize.Scan(sources, _token).Count;
        foreach (var src in sources)
        {
            _token.ThrowIfCancellationRequested();
            DeleteRecursive(src, wipe: false);
        }
    }

    public void Wipe(IReadOnlyList<IPath> sources)
    {
        var cs = FilesCountAndSize.Scan(sources, _token);
        _totalFiles = cs.Count;
        _totalBytes = cs.TotalSize * _wipePasses;
        foreach (var src in sources)
        {
            _token.ThrowIfCancellationRequested();
            DeleteRecursive(src, wipe: true);
        }
    }

    // ---- copy ----------------------------------------------------------

    private void CopyRecursive(IPath src, IDirectory destDir)
    {
        _token.ThrowIfCancellationRequested();
        if (src.IsDirectory())
        {
            var dir = src.GetDirectory();
            var childDir = EnsureChildDirectory(destDir, dir.GetName());
            if (childDir == null) return; // skipped
            using var contents = dir.List();
            foreach (var child in contents) CopyRecursive(child, childDir);
        }
        else if (src.IsFile())
        {
            var file = src.GetFile();
            CopyFile(file, destDir, file.GetName());
        }
    }

    private void CopyFile(IFile srcFile, IDirectory destDir, string name)
    {
        var target = destDir.Path.Combine(name);
        if (target.Exists())
        {
            switch (Resolve(target))
            {
                case OverwriteAction.Skip: return;
                case OverwriteAction.Abort: throw new OperationCanceledException("Aborted on conflict: " + target.PathString);
                default: target.GetFile().Delete(); break;
            }
        }

        _currentFile = name;
        _bytesBeforeCurrent = _bytesDone;
        long size = srcFile.GetSize();
        using (var output = target.GetFile().GetOutputStream())
            srcFile.CopyToOutputStream(output, 0, size, new Adapter(this));
        _bytesDone = _bytesBeforeCurrent + size;
        _filesDone++;
        Report();
    }

    private IDirectory? EnsureChildDirectory(IDirectory parent, string name)
    {
        var target = parent.Path.Combine(name);
        if (target.Exists())
        {
            if (target.IsDirectory()) return target.GetDirectory();
            switch (Resolve(target))
            {
                case OverwriteAction.Skip: return null;
                case OverwriteAction.Abort: throw new OperationCanceledException("Aborted on conflict: " + target.PathString);
                default: target.GetFile().Delete(); break;
            }
        }
        return parent.CreateDirectory(name);
    }

    // ---- move ----------------------------------------------------------

    private void MoveOne(IPath src, IDirectory destDir)
    {
        bool isDir = src.IsDirectory();
        bool sameFs = ReferenceEquals(src.FileSystem, destDir.Path.FileSystem);
        IFsRecord record = isDir ? src.GetDirectory() : src.GetFile();
        var target = destDir.Path.Combine(record.GetName());

        if (sameFs && !target.Exists())
        {
            // MoveTo relocates directory entries without streaming bytes; count the
            // affected files up front, since the source is gone afterwards.
            long movedFiles = isDir ? FilesCountAndSize.Scan(new[] { src }, _token).Count : 1;
            record.MoveTo(destDir);
            _filesDone += movedFiles;
            _currentFile = record.GetName();
            Report();
            return;
        }

        // Cross-filesystem (or conflicting target): copy then delete the source.
        CopyRecursive(src, destDir);
        DeleteRecursive(src, wipe: false);
    }

    // ---- delete / wipe -------------------------------------------------

    private void DeleteRecursive(IPath src, bool wipe)
    {
        _token.ThrowIfCancellationRequested();
        if (src.IsDirectory())
        {
            var dir = src.GetDirectory();
            using (var contents = dir.List())
                foreach (var child in contents) DeleteRecursive(child, wipe);
            dir.Delete();
        }
        else if (src.IsFile())
        {
            var file = src.GetFile();
            _currentFile = file.GetName();
            if (wipe)
                WipeUtil.Wipe(file, _wipePasses, _token, OnWipeProgress);
            file.Delete();
            _filesDone++;
            Report();
        }
    }

    private void OnWipeProgress(long deltaBytes)
    {
        _bytesDone += deltaBytes;
        Report();
    }

    // ---- helpers -------------------------------------------------------

    private void Prescan(IReadOnlyList<IPath> sources)
    {
        var cs = FilesCountAndSize.Scan(sources, _token);
        _totalFiles = cs.Count;
        _totalBytes = cs.TotalSize;
    }

    private OverwriteAction Resolve(IPath target)
        => _onConflict?.Invoke(target) ?? OverwriteAction.Overwrite;

    private void Report()
        => _progress?.Report(new FileOperationStatus(_currentFile, _filesDone, _totalFiles, _bytesDone, _totalBytes));

    /// <summary>Bridges the per-file <see cref="IFileProgressInfo"/> callback to the running totals.</summary>
    private sealed class Adapter(FileOperations owner) : IFileProgressInfo
    {
        public void SetProcessed(long num)
        {
            owner._bytesDone = owner._bytesBeforeCurrent + num;
            owner.Report();
        }

        public bool IsCancelled => owner._token.IsCancellationRequested;
    }
}
