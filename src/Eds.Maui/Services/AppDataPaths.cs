namespace Eds.Maui.Services;

/// <summary>
/// Resolves the per-user application data directory. On the native MAUI heads
/// <c>FileSystem.AppDataDirectory</c> is the right answer, but on the Avalonia
/// (net11.0) head the preview Essentials assembly is a reference-only stub whose
/// <c>FileSystem.AppDataDirectory</c> throws
/// <c>NotImplementedInReferenceAssemblyException</c>. So on that head we compute the
/// path with plain BCL APIs (XDG on Linux, %AppData% on Windows, Application Support
/// on macOS), which never throws and is stable across runs.
/// </summary>
public static class AppDataPaths
{
    public static string AppDataDir()
    {
#if AVALONIA_DESKTOP
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            baseDir = !string.IsNullOrEmpty(xdg)
                ? xdg
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share");
        }
        string dir = Path.Combine(baseDir, "EdsLite");
        Directory.CreateDirectory(dir);
        return dir;
#else
        return Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
#endif
    }
}
