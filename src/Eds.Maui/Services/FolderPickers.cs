namespace Eds.Maui.Services;

#if WITHOUT_COMMUNITYTOOLKIT

/// <summary>
/// Avalonia/desktop head (net11.0). The Avalonia MAUI Essentials preview does not
/// yet expose <c>Microsoft.Maui.Storage.FolderPicker</c>, so rather than depend on a
/// type that may be absent, this returns an unsupported/cancelled result. The
/// calling view-models already fall back to manual path entry when a pick is
/// cancelled, so folder-based actions still work by typing/pasting a path.
///
/// When the preview gains a folder picker (or to wire Avalonia's own
/// IStorageProvider.OpenFolderPickerAsync), replace the body here — the
/// <see cref="IFolderPicker"/> contract stays the same.
/// </summary>
public sealed class EssentialsFolderPicker : IFolderPicker
{
    public Task<FolderPickResult> PickAsync(CancellationToken ct = default)
        => Task.FromResult(new FolderPickResult(null));
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
#endif
