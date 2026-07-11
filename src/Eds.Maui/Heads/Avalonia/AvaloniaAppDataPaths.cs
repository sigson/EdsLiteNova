using System;
using System.IO;

namespace Eds.Maui.Services;

public static partial class AppDataPaths
{
    // XDG on Linux, %AppData% on Windows, Application Support on macOS. Never throws
    // (the preview Essentials FileSystem.AppDataDirectory does).
    public static string AppDataDir()
    {
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
    }
}
