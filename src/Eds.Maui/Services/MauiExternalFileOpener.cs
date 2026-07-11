using Eds.Core.Services;

namespace Eds.Maui.Services;

/// <summary>
/// MAUI implementation of <see cref="IExternalFileOpener"/> using the platform
/// launcher. Hands the decrypted temp file to the OS "open with" mechanism
/// (Android ACTION_VIEW, Windows/macOS shell open, iOS document interaction).
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
#if AVALONIA_DESKTOP
        // MAUI Essentials Launcher is a reference-only stub on the Avalonia preview
        // and throws, so open with the OS default handler directly.
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempFilePath) { UseShellExecute = true });
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", "\"" + tempFilePath.Replace("\"", "\\\"") + "\"");
            else
                System.Diagnostics.Process.Start("xdg-open", "\"" + tempFilePath.Replace("\"", "\\\"") + "\"");
        }
        catch { /* let the browser's save-changes flow proceed regardless */ }
        await Task.CompletedTask;
#else
        var request = new OpenFileRequest
        {
            Title = "Open",
            File = new ReadOnlyFile(tempFilePath, mimeType ?? "application/octet-stream"),
        };
        await Launcher.Default.OpenAsync(request);
#endif
    }
}
