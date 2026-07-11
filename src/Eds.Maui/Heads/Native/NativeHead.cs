using CommunityToolkit.Maui;
using Eds.Core.Locations;
using Eds.Core.Services;
using Eds.Maui.Heads.Native;
using Eds.Maui.Services;
using Microsoft.Extensions.Logging;

namespace Eds.Maui;

/// <summary>
/// Platform registration for the native MAUI heads (Android / iOS / MacCatalyst /
/// Windows). Uses the real MAUI Essentials + CommunityToolkit implementations. This
/// file is compiled for every TFM EXCEPT the Avalonia desktop head (net11.0) — the
/// csproj routes it via a Compile item, so the Avalonia build never sees it.
/// </summary>
public static partial class MauiProgram
{
    internal static void RegisterPlatformServices(MauiAppBuilder builder)
    {
        builder.UseMauiCommunityToolkit();

        builder.Services.AddSingleton<IExternalFileOpener, MauiExternalFileOpener>();
        builder.Services.AddSingleton<IFolderPicker, CommunityFolderPicker>();
        builder.Services.AddSingleton<IFilePickerService, MauiFilePickerService>();

        // Real secret store: Android Keystore / iOS+macOS Keychain / Windows DPAPI.
        builder.Services.AddSingleton<IProtectionKeyProvider, SecureStoreProtectionKeyProvider>();

#if ANDROID
        builder.Services.AddSingleton<IOperationNotifier, AndroidOperationNotifier>();
        builder.Services.AddSingleton<IForegroundOperationService, AndroidForegroundOperationService>();
#else
        builder.Services.AddSingleton<IOperationNotifier, NoopOperationNotifier>();
        builder.Services.AddSingleton<IForegroundOperationService, NoopForegroundOperationService>();
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif
    }
}
