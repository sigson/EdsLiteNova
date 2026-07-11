namespace Eds.Maui.Services;

#if AVALONIA_DESKTOP
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

/// <summary>
/// Locates Avalonia's <see cref="IStorageProvider"/> from the running desktop
/// lifetime. The Avalonia MAUI backend hosts the MAUI UI inside a classic desktop
/// window, so the top-level window is the storage-provider owner.
/// </summary>
internal static class AvaloniaStorage
{
    public static IStorageProvider? Provider
    {
        get
        {
            if (global::Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var w = desktop.MainWindow ?? desktop.Windows.FirstOrDefault();
                return w?.StorageProvider;
            }
            return null;
        }
    }
}

/// <summary>
/// Avalonia head: real native folder dialog via IStorageProvider. (MAUI Essentials /
/// CommunityToolkit pickers are reference-only stubs on this head and throw.)
/// </summary>
public sealed class EssentialsFolderPicker : IFolderPicker
{
    public async Task<FolderPickResult> PickAsync(CancellationToken ct = default)
    {
        var sp = AvaloniaStorage.Provider;
        if (sp == null) return new FolderPickResult(null);

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder",
            AllowMultiple = false,
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        return new FolderPickResult(path);
    }
}

/// <summary>Avalonia head: real native file dialog via IStorageProvider.</summary>
public sealed class AvaloniaFilePickerService : IFilePickerService
{
    public async Task<FilePickResult> PickFileAsync(CancellationToken ct = default)
    {
        var sp = AvaloniaStorage.Provider;
        if (sp == null) return new FilePickResult(null, null);

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select file",
            AllowMultiple = false,
        });
        if (files.Count == 0) return new FilePickResult(null, null);

        var f = files[0];
        return new FilePickResult(f.TryGetLocalPath(), f.Name);
    }
}

#else
using CommunityToolkit.Maui.Storage;

/// <summary>Native MAUI heads: CommunityToolkit.Maui's folder picker.</summary>
public sealed class CommunityFolderPicker : IFolderPicker
{
    public async Task<FolderPickResult> PickAsync(CancellationToken ct = default)
    {
        var res = await FolderPicker.Default.PickAsync(ct);
        return new FolderPickResult(res.IsSuccessful ? res.Folder?.Path : null);
    }
}

/// <summary>Native MAUI heads: MAUI Essentials file picker.</summary>
public sealed class MauiFilePickerService : IFilePickerService
{
    public async Task<FilePickResult> PickFileAsync(CancellationToken ct = default)
    {
        var res = await Microsoft.Maui.Storage.FilePicker.Default.PickAsync();
        return res == null
            ? new FilePickResult(null, null)
            : new FilePickResult(res.FullPath, res.FileName);
    }
}
#endif
