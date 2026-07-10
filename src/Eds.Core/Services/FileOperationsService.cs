using Eds.Core.Fs.Vfs;

namespace Eds.Core.Services;

/// <summary>Outcome of a queued file operation.</summary>
public readonly record struct FileOperationResult(bool Success, bool Cancelled, Exception? Error)
{
    public static readonly FileOperationResult Ok = new(true, false, null);
    public static FileOperationResult FromCancel() => new(false, true, null);
    public static FileOperationResult FromError(Exception e) => new(false, false, e);
}

/// <summary>
/// Background file-operations queue. Replaces the Android
/// <c>FileOpsServiceBase</c> (an <c>IntentService</c> dispatched by
/// <c>Intent</c> actions) with a platform-independent, serialized async queue
/// driven by <c>Task</c> + <see cref="CancellationToken"/> + <see cref="IProgress{T}"/>.
/// The platform layer wires foreground notifications (Android) and the "open in
/// external app" flow around this.
/// </summary>
public interface IFileOperationsService
{
    Task<FileOperationResult> CopyAsync(IReadOnlyList<IPath> sources, IDirectory destination,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default);

    Task<FileOperationResult> MoveAsync(IReadOnlyList<IPath> sources, IDirectory destination,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default);

    Task<FileOperationResult> DeleteAsync(IReadOnlyList<IPath> sources,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default);

    Task<FileOperationResult> WipeAsync(IReadOnlyList<IPath> sources, int passes = 1,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IFileOperationsService"/>: runs one operation at a time off
/// the caller's thread (mirroring the original sequential IntentService queue),
/// honouring cancellation and reporting progress. Exceptions are captured into the
/// returned <see cref="FileOperationResult"/> rather than thrown, so a UI can show
/// them without try/catch around every call.
/// </summary>
public sealed class FileOperationsService : IFileOperationsService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<IPath, OverwriteAction>? _onConflict;

    public FileOperationsService(Func<IPath, OverwriteAction>? onConflict = null)
        => _onConflict = onConflict;

    public Task<FileOperationResult> CopyAsync(IReadOnlyList<IPath> sources, IDirectory destination,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default)
        => Run(ops => ops.Copy(sources, destination), progress, ct);

    public Task<FileOperationResult> MoveAsync(IReadOnlyList<IPath> sources, IDirectory destination,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default)
        => Run(ops => ops.Move(sources, destination), progress, ct);

    public Task<FileOperationResult> DeleteAsync(IReadOnlyList<IPath> sources,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default)
        => Run(ops => ops.Delete(sources), progress, ct);

    public Task<FileOperationResult> WipeAsync(IReadOnlyList<IPath> sources, int passes = 1,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default)
        => Run(ops => ops.Wipe(sources), progress, ct, passes);

    private async Task<FileOperationResult> Run(
        Action<FileOperations> work, IProgress<FileOperationStatus>? progress,
        CancellationToken ct, int wipePasses = 1)
    {
        // Acquire the queue slot. A cancelled token throws here *before* the slot is
        // taken, so convert it to a cancelled result without touching the semaphore.
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FileOperationResult.FromCancel();
        }

        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    work(new FileOperations(progress, ct, _onConflict, wipePasses));
                    return FileOperationResult.Ok;
                }
                catch (OperationCanceledException) { return FileOperationResult.FromCancel(); }
                catch (Exception ex) { return FileOperationResult.FromError(ex); }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FileOperationResult.FromCancel();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
