using System.Linq;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Eds.Maui.Services;

namespace Eds.Maui.Heads.Avalonia;

/// <summary>Locates Avalonia's <see cref="IStorageProvider"/> from the desktop window.</summary>
internal static class AvaloniaStorage
{
    public static IStorageProvider? Provider
    {
        get
        {
            // Fully qualified: `Application` is ambiguous (Avalonia.Application vs
            // Microsoft.Maui.Controls.Application, which MAUI adds as a global using),
            // and a bare `Avalonia.` prefix would collide with this file's own
            // Eds.Maui.Heads.Avalonia namespace.
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

/// <summary>Native folder dialog via Avalonia IStorageProvider.</summary>
public sealed class AvaloniaFolderPicker : IFolderPicker
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

/// <summary>Native file dialog via Avalonia IStorageProvider.</summary>
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
