using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eds.Core.App;
using Eds.Core.Containers.Locations;
using Eds.Core.Fs.Vfs;
using Eds.Core.Locations;
using Eds.Core.Services;
using Eds.Maui.Services;

namespace Eds.Maui.ViewModels;

/// <summary>
/// Browses the filesystem of an already-open location. Lists directories via
/// <see cref="EdsAppController.Browse"/> (sorted), navigates in/up, supports
/// copy/cut/paste (also across locations), new folder, import, delete, external
/// open/edit with write-back, and — for containers — a route to change the password.
/// </summary>
[QueryProperty(nameof(LocationId), "id")]
public partial class VaultBrowserViewModel : ObservableObject
{
    private readonly EdsAppController _app;
    private readonly IOperationNotifier _notifier;
    private ILocation? _location;
    private IFileSystem? _fs;
    private IPath? _current;
    private TempFileHandle? _pendingEdit;

    public ObservableCollection<FileEntry> Items { get; } = new();

    public string[] SortFields { get; } = { "Name", "Size", "Date", "Type" };

    [ObservableProperty] private string _locationId = "";
    [ObservableProperty] private string _title = "Browse";
    [ObservableProperty] private string _breadcrumb = "/";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _canGoUp;
    [ObservableProperty] private bool _writable;
    [ObservableProperty] private bool _canChangePassword;
    [ObservableProperty] private bool _canPaste;
    [ObservableProperty] private bool _hasPendingEdit;
    [ObservableProperty] private string _newFolderName = "";
    [ObservableProperty] private int _selectedSortIndex;
    [ObservableProperty] private bool _descending;
    [ObservableProperty] private bool _directoriesFirst = true;

    public VaultBrowserViewModel(EdsAppController app, IOperationNotifier notifier)
    {
        _app = app;
        _notifier = notifier;
    }

    partial void OnLocationIdChanged(string value)
    {
        _location = _app.FindLocation(value);
        Title = _location?.GetTitle() ?? "Browse";
        Writable = _location is { } l && !l.IsReadOnly();
        CanChangePassword = _location is ContainerLocation;
        try
        {
            _fs = _location?.GetFileSystem();
            _current = _fs?.GetRootPath();
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        RefreshListing();
    }

    partial void OnSelectedSortIndexChanged(int value) => RefreshListing();
    partial void OnDescendingChanged(bool value) => RefreshListing();
    partial void OnDirectoriesFirstChanged(bool value) => RefreshListing();

    private FileListOptions BuildOptions() => new()
    {
        Field = SelectedSortIndex switch
        {
            1 => FileSortField.Size,
            2 => FileSortField.LastModified,
            3 => FileSortField.Type,
            _ => FileSortField.Name,
        },
        Direction = Descending ? SortDirection.Descending : SortDirection.Ascending,
        DirectoriesFirst = DirectoriesFirst,
    };

    private void RefreshListing()
    {
        Items.Clear();
        CanPaste = _app.ClipboardHasItems;
        if (_fs == null || _current == null) return;
        Breadcrumb = _current.PathString;
        CanGoUp = !_current.IsRootDirectory();
        try
        {
            foreach (var item in _app.Browse(_current, BuildOptions()))
                Items.Add(new FileEntry(item));
            Status = Items.Count == 0 ? "(empty)" : "";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task Open(FileEntry? entry)
    {
        if (entry == null) return;
        if (entry.IsDirectory)
        {
            _current = entry.Path;
            RefreshListing();
            return;
        }
        if (!_app.CanOpenExternally)
        {
            Status = $"{entry.Name} — {entry.Detail}";
            return;
        }
        try
        {
            _pendingEdit = _app.PrepareTempFile(entry.Path.GetFile());
            HasPendingEdit = true;
            await _app.LaunchExternalAsync(_pendingEdit.TempPath, GuessMime(entry.Name));
            Status = "Opened externally. Tap “Save changes” after editing to write it back.";
        }
        catch (Exception ex) { Status = "Error: " + ex.Message; }
    }

    [RelayCommand]
    private void SaveEdit()
    {
        if (_pendingEdit == null) return;
        try
        {
            bool saved = _app.SaveTempChanges(_pendingEdit);
            Status = saved ? "Changes saved back and re-encrypted." : "No changes detected.";
        }
        catch (Exception ex) { Status = "Error: " + ex.Message; }
        finally
        {
            _app.ClearTempFile(_pendingEdit);
            _pendingEdit = null;
            HasPendingEdit = false;
            RefreshListing();
        }
    }

    [RelayCommand]
    private void Up()
    {
        if (_current == null || _current.IsRootDirectory()) return;
        _current = _current.GetParentPath();
        RefreshListing();
    }

    [RelayCommand]
    private void NewFolder()
    {
        if (_current == null || string.IsNullOrWhiteSpace(NewFolderName)) return;
        try
        {
            _current.GetDirectory().CreateDirectory(NewFolderName.Trim());
            NewFolderName = "";
            RefreshListing();
        }
        catch (Exception ex) { Status = "Error: " + ex.Message; }
    }

    [RelayCommand]
    private void Copy(FileEntry? entry)
    {
        if (entry == null) return;
        _app.SetClipboard(new[] { entry.Path }, cut: false);
        CanPaste = _app.ClipboardHasItems;
        Status = $"Copied {entry.Name}.";
    }

    [RelayCommand]
    private void Cut(FileEntry? entry)
    {
        if (entry == null) return;
        _app.SetClipboard(new[] { entry.Path }, cut: true);
        CanPaste = _app.ClipboardHasItems;
        Status = $"Cut {entry.Name}.";
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (_current == null || !_app.ClipboardHasItems) return;
        _notifier.Show(1, "EDS Lite", "Pasting…");
        try
        {
            var res = await _app.PasteAsync(_current.GetDirectory());
            if (!res.Success && res.Error != null) Status = "Error: " + res.Error.Message;
        }
        finally { _notifier.Cancel(1); }
        CanPaste = _app.ClipboardHasItems;
        RefreshListing();
    }

    [RelayCommand]
    private async Task DeleteAsync(FileEntry? entry)
    {
        if (entry == null) return;
        bool ok = await Shell.Current.DisplayAlert("Delete", $"Delete \"{entry.Name}\"?", "Delete", "Cancel");
        if (!ok) return;
        var res = await _app.DeleteAsync(new[] { entry.Path });
        if (!res.Success && res.Error != null) Status = "Error: " + res.Error.Message;
        RefreshListing();
    }

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        if (_current == null) return;
        _notifier.Show(2, "EDS Lite", "Importing…");
        try
        {
            var picked = await FilePicker.Default.PickAsync();
            if (picked == null) return;
            await using var input = await picked.OpenReadAsync();
            var target = _current.Combine(picked.FileName).GetFile();
            await using (var output = target.GetOutputStream())
                await input.CopyToAsync(output);
            RefreshListing();
        }
        catch (Exception ex) { Status = "Error: " + ex.Message; }
        finally { _notifier.Cancel(2); }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        await Shell.Current.GoToAsync($"changepw?id={Uri.EscapeDataString(LocationId)}");
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (_pendingEdit != null)
        {
            _app.ClearTempFile(_pendingEdit);
            _pendingEdit = null;
            HasPendingEdit = false;
        }
        if (_location != null)
        {
            try { _app.Close(_location); } catch { /* ignore */ }
        }
        await Shell.Current.GoToAsync("//LocationsPage");
    }

    private static string GuessMime(string name)
    {
        int dot = name.LastIndexOf('.');
        string ext = dot < 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
        return ext switch
        {
            "txt" or "log" or "md" or "csv" => "text/plain",
            "pdf" => "application/pdf",
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "html" or "htm" => "text/html",
            "json" => "application/json",
            "zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }
}

/// <summary>Display wrapper for a file/directory row.</summary>
public sealed class FileEntry
{
    public IPath Path { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public long Size { get; }

    public string Glyph => IsDirectory ? "📁" : "📄";
    public string Detail => IsDirectory ? "" : FormatSize(Size);

    public FileEntry(FileListItem item)
    {
        Path = item.Path;
        Name = item.Name;
        IsDirectory = item.IsDirectory;
        Size = item.Size;
    }

    private static string FormatSize(long n)
        => n < 1024 ? $"{n} B"
         : n < 1024 * 1024 ? $"{n / 1024.0:0.#} KB"
         : $"{n / 1024.0 / 1024.0:0.#} MB";
}
