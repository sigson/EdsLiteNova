using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eds.Core.App;
using Eds.Core.Settings;

namespace Eds.Maui.ViewModels;

/// <summary>
/// Program settings: default container auto-close timeout and history behaviour,
/// plus quick lock / temp-clear actions. Persisted via the shared
/// <see cref="ISettings"/> (a <see cref="JsonFileSettings"/> in the app data dir).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly EdsAppController _app;
    private readonly InMemorySettings? _settings;

    [ObservableProperty] private bool _neverSaveHistory;
    [ObservableProperty] private int _autoCloseSeconds;
    [ObservableProperty] private string _status = "";

    public SettingsViewModel(EdsAppController app)
    {
        _app = app;
        _settings = app.Settings as InMemorySettings;
        _neverSaveHistory = app.Settings.NeverSaveHistory;
        _autoCloseSeconds = app.Settings.MaxContainerInactivityTime;
    }

    partial void OnNeverSaveHistoryChanged(bool value)
    {
        if (_settings != null) _settings.NeverSaveHistory = value;
    }

    partial void OnAutoCloseSecondsChanged(int value)
    {
        if (_settings != null) _settings.MaxContainerInactivityTime = Math.Max(0, value);
    }

    [RelayCommand]
    private void LockAll()
    {
        _app.CloseAll(force: true);
        _app.ClearTempFiles();
        Status = "All locations locked.";
    }

    [RelayCommand]
    private void ClearTemp()
    {
        _app.ClearTempFiles();
        Status = "Temporary decrypted files cleared.";
    }
}
