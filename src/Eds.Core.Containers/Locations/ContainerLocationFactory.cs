using Eds.Core.Locations;
using Eds.Core.Settings;

namespace Eds.Core.Containers.Locations;

/// <summary>
/// Registers the <c>eds-container</c> scheme with a
/// <see cref="LocationsManager"/>. The host calls
/// <c>manager.RegisterCoreFactories().RegisterFactory(new ContainerLocationFactory())</c>
/// at start-up. Kept out of Core so the registry has no compile-time dependency on
/// the Containers project.
/// </summary>
public sealed class ContainerLocationFactory : ILocationFactory
{
    public string Scheme => ContainerLocation.UriSchemeConst;

    public string GetLocationId(LocationsManager manager, LocationUri uri)
        => ContainerLocation.GetLocationId(manager.ResolveBaseLocation(uri));

    public ILocation CreateFromUri(LocationsManager manager, ISettings settings, LocationUri uri)
    {
        var baseLocation = manager.ResolveBaseLocation(uri);
        var loc = new ContainerLocation(settings, baseLocation);
        loc.LoadFromUri(uri);
        return loc;
    }
}
