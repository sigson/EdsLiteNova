using CommunityToolkit.Maui.Storage;
using Eds.Maui.Services;

namespace Eds.Maui.Heads.Native;

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
