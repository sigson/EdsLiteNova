using Eds.Core.Settings;

namespace Eds.Core.Locations;

/// <summary>
/// Creates locations of one URI scheme. This is the seam that keeps
/// <c>Eds.Core</c> free of a dependency on <c>Eds.Core.Containers</c>: container
/// locations live in the Containers project and register their factory with the
/// manager at start-up, while device/EncFS factories ship in Core. Replaces the
/// hard-coded <c>switch(scheme)</c> of <c>LocationsManagerBase.createLocationFromUri</c>.
/// </summary>
public interface ILocationFactory
{
    /// <summary>The URI scheme this factory handles (e.g. "file", "encfs", "eds-container").</summary>
    string Scheme { get; }

    /// <summary>
    /// Computes the stable location id for a URI without fully materialising the
    /// location (may resolve a nested base location via the manager).
    /// </summary>
    string GetLocationId(LocationsManager manager, LocationUri uri);

    /// <summary>Materialises a location from its URI.</summary>
    ILocation CreateFromUri(LocationsManager manager, ISettings settings, LocationUri uri);
}

/// <summary>Factory for plain device folders (<see cref="DeviceLocation"/>).</summary>
public sealed class DeviceLocationFactory : ILocationFactory
{
    public string Scheme => DeviceLocation.UriScheme;

    public string GetLocationId(LocationsManager manager, LocationUri uri)
        => DeviceLocation.GetLocationId(uri);

    public ILocation CreateFromUri(LocationsManager manager, ISettings settings, LocationUri uri)
    {
        var loc = new DeviceLocation(settings, uri.GetQueryParameter("root") ?? "");
        loc.LoadFromUri(uri);
        return loc;
    }
}

/// <summary>Factory for EncFS volumes (<see cref="EncFsLocation"/>).</summary>
public sealed class EncFsLocationFactory : ILocationFactory
{
    public string Scheme => EncFsLocation.UriSchemeConst;

    public string GetLocationId(LocationsManager manager, LocationUri uri)
        => EncFsLocation.GetLocationId(manager.ResolveBaseLocation(uri));

    public ILocation CreateFromUri(LocationsManager manager, ISettings settings, LocationUri uri)
    {
        var baseLocation = manager.ResolveBaseLocation(uri);
        var loc = new EncFsLocation(settings, baseLocation);
        loc.LoadFromUri(uri);
        return loc;
    }
}
