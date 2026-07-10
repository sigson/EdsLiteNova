namespace Eds.Maui.Services;

/// <summary>
/// Bridges DI to components that Android/iOS instantiate outside the container
/// (e.g. a <c>DocumentsProvider</c>). Set once at app startup.
/// </summary>
public static class AppServices
{
    public static IServiceProvider? Services { get; set; }

    public static T? Get<T>() where T : class
        => Services?.GetService(typeof(T)) as T;
}
