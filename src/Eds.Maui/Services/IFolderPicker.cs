namespace Eds.Maui.Services;

/// <summary>Result of a folder pick: a local filesystem path, or null if cancelled.</summary>
public readonly record struct FolderPickResult(string? Path)
{
    public bool IsSuccessful => !string.IsNullOrEmpty(Path);
}

/// <summary>
/// Abstracts folder selection so the view-models don't depend on a specific picker.
/// On the native MAUI heads this is backed by CommunityToolkit.Maui's FolderPicker;
/// on the Avalonia/Linux head (net11.0, where CommunityToolkit.Maui isn't referenced)
/// it uses the built-in MAUI Essentials folder picker. Keeps the VMs identical
/// across heads.
/// </summary>
public interface IFolderPicker
{
    Task<FolderPickResult> PickAsync(CancellationToken ct = default);
}
