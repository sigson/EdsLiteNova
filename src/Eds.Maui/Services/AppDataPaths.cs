namespace Eds.Maui.Services;

/// <summary>
/// Resolves the per-user application data directory. The actual implementation
/// (<c>AppDataDir()</c>) is defined once per head — Heads/Native (native:
/// FileSystem.AppDataDirectory) and Heads/Avalonia (BCL paths, because the
/// preview Essentials FileSystem stub throws). Only one head file compiles per TFM,
/// so there is exactly one definition. No <c>#if</c> here.
/// </summary>
public static partial class AppDataPaths
{
}
