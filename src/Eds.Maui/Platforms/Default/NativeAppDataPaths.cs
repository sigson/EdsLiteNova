namespace Eds.Maui.Services;

public static partial class AppDataPaths
{
    static partial void PlatformAppDataDir(ref string dir)
        => dir = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
}
