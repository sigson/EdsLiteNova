using Eds.Core.Services;

namespace Eds.Maui.Services;

/// <summary>
/// MAUI implementation of <see cref="IExternalFileOpener"/> for the native heads,
/// using the platform launcher (Android ACTION_VIEW, Windows/macOS shell open, iOS
/// document interaction). The Avalonia head uses DesktopExternalFileOpener instead.
///
/// <para>The launcher returns as soon as the external app is invoked; it does not
/// block until editing finishes. The browser therefore uses the granular
/// prepare → launch → "save changes" flow (on <see cref="Eds.Core.App.EdsAppController"/>)
/// so the user can write edits back explicitly.</para>
/// </summary>
public sealed class MauiExternalFileOpener : IExternalFileOpener
{
    public async Task OpenAsync(string tempFilePath, string? mimeType, CancellationToken ct)
    {
        var request = new OpenFileRequest
        {
            Title = "Open",
            File = new ReadOnlyFile(tempFilePath, mimeType ?? "application/octet-stream"),
        };
        await Launcher.Default.OpenAsync(request);
    }
}
