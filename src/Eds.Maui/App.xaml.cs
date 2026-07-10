using Eds.Core.App;

namespace Eds.Maui;

public partial class App : Application
{
    private readonly EdsAppController _controller;
    private CancellationTokenSource? _autoCloseCts;

    public App(EdsAppController controller)
    {
        InitializeComponent();
        _controller = controller;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());

        // Idle auto-close loop (per-location inactivity timeout from settings).
        window.Created += (_, _) =>
        {
            if (_autoCloseCts != null) return;
            _autoCloseCts = new CancellationTokenSource();
            _ = _controller.StartAutoCloseAsync(TimeSpan.FromSeconds(30), _autoCloseCts.Token);
        };

        // Lock everything and wipe temp files when the window goes away.
        window.Destroying += (_, _) => LockAll();

        return window;
    }

    private void LockAll()
    {
        try { _controller.CloseAll(force: true); } catch { /* ignore */ }
        try { _controller.ClearTempFiles(); } catch { /* ignore */ }
        _autoCloseCts?.Cancel();
    }
}
