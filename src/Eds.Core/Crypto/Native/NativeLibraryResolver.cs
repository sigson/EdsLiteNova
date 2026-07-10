using System.Reflection;
using System.Runtime.InteropServices;

namespace Eds.Core.Crypto.Native;

/// <summary>
/// Installs a DllImportResolver so that the single logical name "edscrypto"
/// resolves correctly on every platform:
///   - iOS / Mac Catalyst (static link): "__Internal"
///   - Android / Linux: libedscrypto.so
///   - Windows: edscrypto.dll
///   - macOS: libedscrypto.dylib
///
/// Call <see cref="EnsureRegistered"/> once at startup (the crypto classes do
/// this lazily via a static constructor, so manual calls are optional).
/// </summary>
public static class NativeLibraryResolver
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            return;

        NativeLibrary.SetDllImportResolver(
            typeof(NativeCrypto).Assembly,
            Resolve);
    }

    /// <summary>
    /// Version of the loaded native crypto library (a public facade over the
    /// internal P/Invoke surface, e.g. for diagnostics/banners).
    /// </summary>
    public static int NativeVersion
    {
        get
        {
            EnsureRegistered();
            return NativeCrypto.Version();
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeCrypto.Lib)
            return nint.Zero; // fall back to default resolution

        // iOS and Mac Catalyst require static linking; the symbols live in the
        // main executable image, referenced as "__Internal".
        if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst())
        {
            if (NativeLibrary.TryLoad("__Internal", assembly, searchPath, out var internalHandle))
                return internalHandle;
        }

        // Explicitly probe likely locations. This makes `dotnet test`/`dotnet run`
        // work on desktop even when the SDK does not copy runtimes/<rid>/native
        // into the output folder (which happens for RID-less builds).
        foreach (var candidate in CandidatePaths(assembly))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h))
                return h;
        }

        // Default: let the runtime map the logical name to the platform file
        // (libedscrypto.so / edscrypto.dll / libedscrypto.dylib) via its own search.
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle)
            ? handle
            : nint.Zero;
    }

    private static IEnumerable<string> CandidatePaths(Assembly assembly)
    {
        string file = PlatformFileName();
        string rid = CurrentRid();

        var roots = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir)) roots.Add(baseDir);
        var asmDir = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrEmpty(asmDir) && asmDir != baseDir) roots.Add(asmDir);

        foreach (var root in roots)
        {
            yield return Path.Combine(root, file);
            yield return Path.Combine(root, "runtimes", rid, "native", file);
        }
    }

    private static string PlatformFileName()
    {
        if (OperatingSystem.IsWindows()) return "edscrypto.dll";
        if (OperatingSystem.IsMacOS()) return "libedscrypto.dylib";
        return "libedscrypto.so";
    }

    private static string CurrentRid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
                  : OperatingSystem.IsMacOS() ? "osx"
                  : "linux";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }
}
