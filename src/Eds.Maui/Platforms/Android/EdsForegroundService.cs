using Android.App;
using Android.Content;
using Android.OS;
using AndroidApp = Android.App.Application;

namespace Eds.Maui.Services;

/// <summary>
/// Android started/foreground <see cref="Service"/> that keeps EDS alive during a
/// long file operation (copy/move/import/wipe). While at least one operation scope
/// is active the service runs in the foreground with an ongoing notification, which
/// stops Android from killing the process mid-operation (and satisfies the
/// background-execution limits on API 26+). It is a plain started service (not
/// bound): scopes call <see cref="StartForeground"/>/<see cref="StopForeground"/>
/// via static Start/Update/Stop helpers.
/// </summary>
[Service(Exported = false, ForegroundServiceType =
    global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class EdsForegroundService : Service
{
    public const string ChannelId = "eds_file_ops_fg";
    private const int NotificationId = 0x0ED5;

    public const string ActionStart = "eds.fg.start";
    public const string ActionUpdate = "eds.fg.update";
    public const string ActionStop = "eds.fg.stop";
    public const string ExtraTitle = "title";
    public const string ExtraText = "text";
    public const string ExtraProgress = "progress"; // -1 == indeterminate

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string action = intent?.Action ?? ActionStart;
        if (action == ActionStop)
        {
            StopForegroundCompat();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        string title = intent?.GetStringExtra(ExtraTitle) ?? "EDS Lite";
        string text = intent?.GetStringExtra(ExtraText) ?? "Working…";
        int progress = intent?.GetIntExtra(ExtraProgress, -1) ?? -1;

        EnsureChannel();
        StartForeground(NotificationId, BuildNotification(title, text, progress));
        return StartCommandResult.Sticky;
    }

    private Notification BuildNotification(string title, string text, int progress)
    {
        var ctx = (Context)this;
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(ctx, ChannelId)
            : new Notification.Builder(ctx);

        builder.SetContentTitle(title)
               .SetContentText(text)
               .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
               .SetOngoing(true);

        if (progress >= 0)
            builder.SetProgress(100, Math.Clamp(progress, 0, 100), false);
        else
            builder.SetProgress(0, 0, true); // indeterminate

        return builder.Build();
    }

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        if (mgr?.GetNotificationChannel(ChannelId) == null)
            mgr?.CreateNotificationChannel(
                new NotificationChannel(ChannelId, "File operations", NotificationImportance.Low));
    }

    private void StopForegroundCompat()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
            StopForeground(StopForegroundFlags.Remove);
        else
#pragma warning disable CA1422 // deprecated below API 24
            StopForeground(true);
#pragma warning restore CA1422
    }

    // ---- static drivers used by the service impl ----------------------
    internal static void Post(string action, string? title, string? text, int? progress)
    {
        var ctx = AndroidApp.Context;
        var intent = new Intent(ctx, typeof(EdsForegroundService)).SetAction(action);
        if (title != null) intent.PutExtra(ExtraTitle, title);
        if (text != null) intent.PutExtra(ExtraText, text);
        intent.PutExtra(ExtraProgress, progress ?? -1);

        if (action != ActionStop && Build.VERSION.SdkInt >= BuildVersionCodes.O)
            ctx.StartForegroundService(intent);
        else
            ctx.StartService(intent);
    }
}

/// <summary>Android implementation of <see cref="IForegroundOperationService"/>.</summary>
public sealed class AndroidForegroundOperationService : IForegroundOperationService
{
    private int _active;

    public IForegroundOperationScope Begin(string title, string? text = null)
    {
        Interlocked.Increment(ref _active);
        EdsForegroundService.Post(EdsForegroundService.ActionStart, title, text ?? "Working…", null);
        return new Scope(this, title);
    }

    private void End()
    {
        // Only tear the service down when the last concurrent operation finishes.
        if (Interlocked.Decrement(ref _active) <= 0)
            EdsForegroundService.Post(EdsForegroundService.ActionStop, null, null, null);
    }

    private sealed class Scope : IForegroundOperationScope
    {
        private readonly AndroidForegroundOperationService _owner;
        private readonly string _title;
        private bool _disposed;

        public Scope(AndroidForegroundOperationService owner, string title)
        {
            _owner = owner;
            _title = title;
        }

        public void Update(string text, int? progressPercent = null)
            => EdsForegroundService.Post(EdsForegroundService.ActionUpdate, _title, text, progressPercent);

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _owner.End();
            }
            return ValueTask.CompletedTask;
        }
    }
}
