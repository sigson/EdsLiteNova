using CommunityToolkit.Maui;
using Eds.Core.App;
using Eds.Core.Crypto.Native;
using Eds.Core.Locations;
using Eds.Core.Services;
using Eds.Core.Settings;
using Eds.Maui.Services;
using Eds.Maui.ViewModels;
using Eds.Maui.Views;
using Microsoft.Extensions.Logging;

namespace Eds.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Ensure the native crypto library resolves correctly on every head.
        NativeLibraryResolver.EnsureRegistered();

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit();

        // --- platform services -----------------------------------------
        builder.Services.AddSingleton<IExternalFileOpener, MauiExternalFileOpener>();
        builder.Services.AddSingleton<IProtectionKeyProvider, SimpleStoreProtectionKeyProvider>();
#if ANDROID
        builder.Services.AddSingleton<IOperationNotifier, AndroidOperationNotifier>();
#else
        builder.Services.AddSingleton<IOperationNotifier, NoopOperationNotifier>();
#endif

        // --- application core (platform-independent) --------------------
        builder.Services.AddSingleton<ISettings>(_ =>
            new JsonFileSettings(Path.Combine(FileSystem.AppDataDirectory, "eds-settings.json")));
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

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Bridge DI to platform-instantiated components (e.g. the SAF provider),
        // and attach the (insecure placeholder) protection-key provider.
        AppServices.Services = app.Services;
        app.Services.GetRequiredService<EdsAppController>().ProtectionKeyProvider =
            app.Services.GetRequiredService<IProtectionKeyProvider>();

        return app;
    }
}
