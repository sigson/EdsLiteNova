using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eds.Core.App;
using Eds.Core.Containers.Locations;
using Eds.Core.Crypto;

namespace Eds.Maui.ViewModels;

/// <summary>
/// Changes the password of an open container location (re-keys the header, keeps
/// the master key). Requires the container to be open read-write.
/// </summary>
[QueryProperty(nameof(LocationId), "id")]
public partial class ChangePasswordViewModel : ObservableObject
{
    private readonly EdsAppController _app;

    [ObservableProperty] private string _locationId = "";
    [ObservableProperty] private string _title = "Change password";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private int _pim;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";

    public ChangePasswordViewModel(EdsAppController app) => _app = app;

    partial void OnLocationIdChanged(string value)
    {
        var loc = _app.FindLocation(value);
        if (loc != null) Title = "Change password — " + loc.GetTitle();
    }

    [RelayCommand]
    private async Task ChangeAsync()
    {
        if (_app.FindLocation(LocationId) is not ContainerLocation loc)
        {
            Status = "Only container locations support changing the password here.";
            return;
        }
        if (!loc.IsOpen())
        {
            Status = "Open the container (read-write) first.";
            return;
        }
        if (NewPassword.Length == 0) { Status = "Enter a new password."; return; }
        if (NewPassword != ConfirmPassword) { Status = "Passwords do not match."; return; }

        Busy = true;
        Status = "Re-keying… key derivation can take a moment.";
        try
        {
            var pw = SecureBuffer.FromPassword(NewPassword);
            NewPassword = "";
            ConfirmPassword = "";
            await _app.ChangeContainerPasswordAsync(loc, pw, newPim: Pim);
            Status = "Password changed.";
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
