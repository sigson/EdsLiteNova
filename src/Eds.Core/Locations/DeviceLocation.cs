using Eds.Core.Fs.Std;
using Eds.Core.Fs.Vfs;
using Eds.Core.Settings;

namespace Eds.Core.Locations;

/// <summary>
/// A plain folder on the device, backed by <see cref="StdFs"/>. Port of the
/// platform-independent essence of <c>locations.DeviceBasedLocation</c>: a root
/// directory (chroot) plus a current path beneath it. This is the "file/folder on
/// device" source and, crucially, the <em>base</em> that container and EncFS
/// locations layer on top of (their current path resolves to the container file /
/// EncFS root through this location's filesystem).
///
/// <para>URI form: <c>file:/&lt;currentPath&gt;?root=&lt;rootDir&gt;</c>. The id is
/// <c>"stdfs" + rootDir</c>, mirroring the original.</para>
/// </summary>
public sealed class DeviceLocation : LocationBase
{
    public const string UriScheme = "file";
    private const string RootParam = "root";

    private string _rootDir;
    private StdFs? _fs;

    public DeviceLocation(ISettings settings, string rootDir, string? currentPath = null) : base(settings)
    {
        _rootDir = rootDir;
        CurrentPathString = currentPath;
    }

    private DeviceLocation(DeviceLocation sibling) : base(sibling.GlobalSettings)
    {
        _rootDir = sibling._rootDir;
        CurrentPathString = sibling.CurrentPathString;
    }

    public string RootDir => _rootDir;

    public static string GetLocationId(string rootDir) => "stdfs" + rootDir;
    public static string GetLocationId(LocationUri uri) => GetLocationId(uri.GetQueryParameter(RootParam) ?? "");

    public override string GetId() => GetLocationId(_rootDir);

    public override IFileSystem GetFileSystem() => _fs ??= new StdFs(_rootDir);

    public override IPath GetCurrentPath()
        => CurrentPathString == null ? GetFileSystem().GetRootPath() : GetFileSystem().GetPath(CurrentPathString);

    public override LocationUri GetLocationUri()
        => new LocationUri(UriScheme, CurrentPathString ?? "/")
            .WithQueryParameter(RootParam, _rootDir);

    public override void LoadFromUri(LocationUri uri)
    {
        var root = uri.GetQueryParameter(RootParam);
        if (root != null) _rootDir = root;
        CurrentPathString = uri.Path == "/" ? null : uri.Path;
    }

    public override string GetTitle()
    {
        var t = GetExternalSettings().Title;
        if (!string.IsNullOrEmpty(t)) return t;
        return CurrentPathString ?? _rootDir;
    }

    public override bool IsDirectlyAccessible() => true;

    public override bool IsFileSystemOpen() => _fs != null;

    public override void CloseFileSystem(bool force)
    {
        _fs?.Close(force);
        _fs = null;
    }

    public override ILocation Copy() => new DeviceLocation(this);

    protected override ExternalSettingsBase CreateExternalSettings() => new DeviceExternalSettings();

    private sealed class DeviceExternalSettings : ExternalSettingsBase { }
}
