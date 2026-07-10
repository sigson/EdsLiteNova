using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eds.Core.App;
using Eds.Core.Containers;
using Eds.Core.Crypto;
using Eds.Core.Locations;

namespace Eds.Maui.ViewModels;

/// <summary>
/// Unlocks an encrypted location. Takes the location id via the "id" route query,
/// collects the password (and optional PIM), opens off the UI thread with live
/// KDF/cipher progress, then navigates to the browser.
/// </summary>
[QueryProperty(nameof(LocationId), "id")]
public partial class OpenViewModel : ObservableObject
{
    private readonly EdsAppController _app;

    [ObservableProperty] private string _locationId = "";
    [ObservableProperty] private string _title = "Unlock";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private int _pim;
    [ObservableProperty] private bool _readOnly;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _kdfInfo = "";

    public OpenViewModel(EdsAppController app) => _app = app;

    partial void OnLocationIdChanged(string value)
    {
        var loc = _app.FindLocation(value);
        if (loc != null) Title = "Unlock " + loc.GetTitle();
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (_app.FindLocation(LocationId) is not IEdsLocation loc)
        {
            Status = "Location not found.";
            return;
        }
        if (Password.Length == 0)
        {
            Status = "Enter the password.";
            return;
        }

        Busy = true;
        Status = "Opening… key derivation can take a few seconds.";
        KdfInfo = "";
        try
        {
            var reporter = new DelegateProgressReporter
            {
                OnKdf = k => KdfInfo = $"trying {k}…",
                OnCipher = c => KdfInfo = $"trying {c}…",
            };
            var pw = SecureBuffer.FromPassword(Password);
            Password = ""; // drop the plaintext copy asap
            await _app.OpenAsync(loc, pw, pim: Pim, readOnly: ReadOnly, reporter: reporter);

            await Shell.Current.GoToAsync($"vault?id={Uri.EscapeDataString(LocationId)}");
        }
        catch (Exception ex)
        {
            Status = "Failed: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
