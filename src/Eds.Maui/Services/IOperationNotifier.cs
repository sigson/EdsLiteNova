namespace Eds.Maui.Services;

/// <summary>
/// Shows/updates a lightweight progress notification for a running file operation.
/// Implemented natively on Android; a no-op elsewhere.
/// </summary>
public interface IOperationNotifier
{
    void Show(int id, string title, string text, int? progressPercent = null);
    void Cancel(int id);
}

/// <summary>Default no-op used on platforms without a native notifier.</summary>
public sealed class NoopOperationNotifier : IOperationNotifier
{
    public void Show(int id, string title, string text, int? progressPercent = null) { }
    public void Cancel(int id) { }
}
