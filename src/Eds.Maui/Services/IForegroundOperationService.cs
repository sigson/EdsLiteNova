namespace Eds.Maui.Services;

/// <summary>
/// Keeps the process alive and user-visible while a long file operation runs.
/// On Android this is a real started/foreground <c>Service</c> holding an ongoing
/// notification (so the OS won't kill mid-copy); elsewhere it is a no-op.
///
/// Usage:
/// <code>
/// await using var scope = _foreground.Begin("Pasting…");
/// scope.Update("Pasting 3/10", 30);
/// // ... work ...
/// </code>
/// Disposing the scope ends the foreground session.
/// </summary>
public interface IForegroundOperationScope : IAsyncDisposable
{
    void Update(string text, int? progressPercent = null);
}

/// <summary>Starts a foreground operation session. Implemented natively on Android.</summary>
public interface IForegroundOperationService
{
    IForegroundOperationScope Begin(string title, string? text = null);
}

/// <summary>No-op scope/service for platforms without a foreground-service concept.</summary>
public sealed class NoopForegroundOperationService : IForegroundOperationService
{
    public IForegroundOperationScope Begin(string title, string? text = null) => NoopScope.Instance;

    private sealed class NoopScope : IForegroundOperationScope
    {
        public static readonly NoopScope Instance = new();
        public void Update(string text, int? progressPercent = null) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
