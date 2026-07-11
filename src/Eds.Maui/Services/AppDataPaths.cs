namespace Eds.Maui.Services;

/// <summary>
/// Resolves the per-user application data directory. The actual path comes from a
/// per-head partial (<see cref="PlatformAppDataDir"/>): native heads return
/// <c>FileSystem.AppDataDirectory</c>; the Avalonia head computes it with BCL APIs
/// because the preview Essentials FileSystem stub throws. No <c>#if</c> here.
/// </summary>
public static partial class AppDataPaths
{
    /// <summary>Implemented once per head (Platforms/Default and Platforms/Avalonia).</summary>
    static partial void PlatformAppDataDir(ref string dir);

    public static string AppDataDir()
    {
        string dir = "";
        PlatformAppDataDir(ref dir);
        return dir;
    }
}
