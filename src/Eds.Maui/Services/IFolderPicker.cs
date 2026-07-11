namespace Eds.Maui.Services;

/// <summary>Result of a folder pick: a local filesystem path, or null if cancelled.</summary>
public readonly record struct FolderPickResult(string? Path)
{
    public bool IsSuccessful => !string.IsNullOrEmpty(Path);
}

/// <summary>Result of a file pick: local path + file name, or null if cancelled.</summary>
public readonly record struct FilePickResult(string? Path, string? FileName)
{
    public bool IsSuccessful => !string.IsNullOrEmpty(Path);
}

/// <summary>
/// Abstracts file/folder selection so the view-models don't depend on a specific
/// picker. On the native MAUI heads this is backed by MAUI Essentials /
/// CommunityToolkit; on the Avalonia (net11.0) head those are reference-only stubs
/// that throw, so it is backed by Avalonia's own IStorageProvider.
/// </summary>
public interface IFolderPicker
{
    Task<FolderPickResult> PickAsync(CancellationToken ct = default);
}

/// <summary>Picks a single existing file.</summary>
public interface IFilePickerService
{
    Task<FilePickResult> PickFileAsync(CancellationToken ct = default);
}
