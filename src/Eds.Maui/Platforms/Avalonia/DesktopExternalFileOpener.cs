using System.Diagnostics;
using System.Runtime.InteropServices;
using Eds.Core.Services;

namespace Eds.Maui.Platforms.Avalonia;

/// <summary>
/// Opens the decrypted temp file with the OS default handler on desktop
/// (<c>xdg-open</c> on Linux, <c>open</c> on macOS, ShellExecute on Windows). Used
/// on the Avalonia head, where MAUI Essentials <c>Launcher</c> is a reference-only
/// stub that throws.
/// </summary>
public sealed class DesktopExternalFileOpener : IExternalFileOpener
{
    public Task OpenAsync(string tempFilePath, string? mimeType, CancellationToken ct)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(tempFilePath) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", Quote(tempFilePath));
            else
                Process.Start("xdg-open", Quote(tempFilePath));
        }
        catch
        {
            // The browser's explicit save-changes flow proceeds regardless.
        }
        return Task.CompletedTask;
    }

    private static string Quote(string p) => "\"" + p.Replace("\"", "\\\"") + "\"";
}
