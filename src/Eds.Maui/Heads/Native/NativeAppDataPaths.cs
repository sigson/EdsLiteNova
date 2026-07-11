namespace Eds.Maui.Services;

public static partial class AppDataPaths
{
    public static string AppDataDir() => Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
}
