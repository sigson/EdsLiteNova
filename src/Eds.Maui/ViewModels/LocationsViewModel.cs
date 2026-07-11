using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eds.Core.App;
using Eds.Core.Locations;
using Eds.Maui.Services;

namespace Eds.Maui.ViewModels;

/// <summary>
/// Home screen: the list of registered locations (containers / EncFS folders /
/// device folders). Add new ones, open them (which routes to the password page for
/// encrypted locations, or straight to the browser for plain folders), close and
/// remove. Binds entirely to <see cref="EdsAppController"/>.
/// </summary>
public partial class LocationsViewModel : ObservableObject
{
    private readonly EdsAppController _app;
    private readonly IFolderPicker _folderPicker;

    public ObservableCollection<LocationItem> Locations { get; } = new();

    [ObservableProperty] private string _status = "";

    public LocationsViewModel(EdsAppController app, IFolderPicker folderPicker)
    {
        _app = app;
        _folderPicker = folderPicker;
        _app.LoadStoredLocations(); // once (singleton VM)
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        Locations.Clear();
        foreach (var loc in _app.GetRegisteredLocations())
            Locations.Add(new LocationItem(loc));
        Status = Locations.Count == 0 ? "No locations yet — add a container or EncFS folder." : "";
    }

    [RelayCommand]
    private async Task AddContainerAsync()
    {
        try
        {
            var picked = await FilePicker.Default.PickAsync();
            if (picked == null) return;
            _app.AddContainerLocation(picked.FullPath);
            Refresh();
        }
        catch (Exception ex) { Status = "Error: " + ex.Message; }
    }

    [RelayCommand]
    private async Task AddEncFsAsync()
    {
        try
        {
            var res = await _folderPicker.PickAsync(CancellationToken.None);
            if (!res.IsSuccessful || res.Path == null) return;
            _app.AddEncFsLocation(res.Path);
            Refresh();
        }
        catch (Exception ex) { Status = "Error: " + ex.Message; }
    }

    [RelayCommand]
    private async Task OpenAsync(LocationItem? item)
    {
        if (item == null) return;
        if (item.Location is IEdsLocation eds && !eds.IsOpen())
        {
            await Shell.Current.GoToAsync($"open?id={Uri.EscapeDataString(item.Id)}");
            return;
        }
        // Already open, or a directly-accessible device folder → browse now.
        await Shell.Current.GoToAsync($"vault?id={Uri.EscapeDataString(item.Id)}");
    }

    [RelayCommand]
    private void Close(LocationItem? item)
    {
        if (item == null) return;
        try { _app.Close(item.Location); item.IsOpen = false; }
        catch (Exception ex) { Status = "Error: " + ex.Message; }
    }

    [RelayCommand]
    private async Task RemoveAsync(LocationItem? item)
    {
        if (item == null) return;
        bool ok = await Shell.Current.DisplayAlert("Remove location",
            $"Remove \"{item.Title}\" from the list? (The file/folder itself is not deleted.)", "Remove", "Cancel");
        if (!ok) return;
        try { _app.Close(item.Location, force: true); } catch { /* ignore */ }
        _app.RemoveLocation(item.Location);
        Refresh();
    }
}

/// <summary>Display wrapper for a location row.</summary>
public partial class LocationItem : ObservableObject
{
    public ILocation Location { get; }
    public string Id { get; }
    public string Title { get; }
    public bool IsEncrypted { get; }

    [ObservableProperty] private bool _isOpen;

    public string Glyph => IsEncrypted ? "🔒" : "📁";
    public string StateText => IsOpen ? "Open" : (IsEncrypted ? "Locked" : "Folder");

    public LocationItem(ILocation loc)
    {
        Location = loc;
        Id = loc.GetId();
        Title = loc.GetTitle();
        IsEncrypted = loc.IsEncrypted();
        _isOpen = loc.IsFileSystemOpen();
    }

    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(StateText));
}
