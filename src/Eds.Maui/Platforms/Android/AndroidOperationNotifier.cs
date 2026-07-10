using Android.App;
using Android.Content;
using Android.OS;
using AndroidApp = Android.App.Application;

namespace Eds.Maui.Services;

/// <summary>
/// Android implementation of <see cref="IOperationNotifier"/> — posts a low-priority
/// (optionally ongoing, with progress) notification for a running file operation.
/// </summary>
public sealed class AndroidOperationNotifier : IOperationNotifier
{
    private const string ChannelId = "eds_file_ops";
    private readonly NotificationManager? _manager;

    public AndroidOperationNotifier()
    {
        var ctx = AndroidApp.Context;
        _manager = ctx.GetSystemService(Context.NotificationService) as NotificationManager;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && _manager != null)
        {
            var channel = new NotificationChannel(ChannelId, "File operations", NotificationImportance.Low);
            _manager.CreateNotificationChannel(channel);
        }
    }

    public void Show(int id, string title, string text, int? progressPercent = null)
    {
        if (_manager == null) return;
        var ctx = AndroidApp.Context;

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(ctx, ChannelId)
            : new Notification.Builder(ctx);

        builder.SetContentTitle(title)
               .SetContentText(text)
               .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
               .SetOngoing(progressPercent.HasValue);

        if (progressPercent.HasValue)
            builder.SetProgress(100, Math.Clamp(progressPercent.Value, 0, 100), false);

        _manager.Notify(id, builder.Build());
    }

    public void Cancel(int id) => _manager?.Cancel(id);
}
