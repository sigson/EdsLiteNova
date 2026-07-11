using Eds.Core.App;
using Eds.Core.Crypto.Native;
using Eds.Core.Locations;
using Eds.Core.Services;
using Eds.Core.Settings;
using Eds.Maui.Services;
using Eds.Maui.ViewModels;
using Eds.Maui.Views;

namespace Eds.Maui;

/// <summary>
/// App composition root. Platform-neutral wiring lives here; anything that differs
/// between the native MAUI heads and the Avalonia (Linux/desktop) head is provided
/// by <see cref="RegisterPlatformServices"/>, a partial method implemented once per
/// head under <c>Platforms/Default</c> (native) and <c>Platforms/Avalonia</c>
/// (net11.0). This keeps the shared file free of <c>#if</c> and keeps the Avalonia
/// preview shims from touching the native builds.
/// </summary>
public static partial class MauiProgram
{
    /// <summary>
    /// Implemented per head. Registers: the MAUI-vs-Avalonia app host, file/folder
    /// pickers, protection-key provider, operation notifier + foreground service,
    /// external file opener, and (native only) logging/toolkit init.
    /// </summary>
    static partial void RegisterPlatformServices(MauiAppBuilder builder);

    public static MauiApp CreateMauiApp()
    {
        // Ensure the native crypto library resolves correctly on every head.
        NativeLibraryResolver.EnsureRegistered();

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Head-specific host + platform services (pickers, key store, notifier, …).
        RegisterPlatformServices(builder);

        // --- application core (platform-independent) --------------------
        builder.Services.AddSingleton<ISettings>(_ =>
            new JsonFileSettings(Path.Combine(AppDataPaths.AppDataDir(), "eds-settings.json")));
        builder.Services.AddSingleton(sp => new EdsAppController(
            sp.GetRequiredService<ISettings>(),
            externalOpener: sp.GetRequiredService<IExternalFileOpener>()));

        // --- pages / view models ---------------------------------------
        builder.Services.AddSingleton<LocationsViewModel>();
        builder.Services.AddSingleton<LocationsPage>();

        builder.Services.AddTransient<OpenViewModel>();
        builder.Services.AddTransient<OpenPage>();

        builder.Services.AddTransient<VaultBrowserViewModel>();
        builder.Services.AddTransient<VaultBrowserPage>();

        builder.Services.AddTransient<ChangePasswordViewModel>();
        builder.Services.AddTransient<ChangePasswordPage>();

        builder.Services.AddTransient<CreateViewModel>();
        builder.Services.AddTransient<CreatePage>();

        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<SettingsPage>();

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainPage>();

        var app = builder.Build();

        // Bridge DI to platform-instantiated components (e.g. the SAF provider) and
        // attach the protection-key provider to the controller.
        AppServices.Services = app.Services;
        app.Services.GetRequiredService<EdsAppController>().ProtectionKeyProvider =
            app.Services.GetRequiredService<IProtectionKeyProvider>();

        return app;
    }
}
