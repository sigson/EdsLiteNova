using System.Text.Json;
using Eds.Core.Crypto;
using Eds.Core.Settings;

namespace Eds.Core.Locations;

/// <summary>Event payload for location registry changes.</summary>
public sealed class LocationEventArgs(ILocation location) : EventArgs
{
    public ILocation Location { get; } = location;
}

/// <summary>
/// The central registry of locations. Platform-independent port of
/// <c>LocationsManagerBase</c>: it loads/saves the list of registered locations
/// (a JSON array of URI strings in <see cref="ISettings"/>), creates locations
/// from URIs via registered <see cref="ILocationFactory"/> instances, keeps a
/// registry of the currently loaded locations, tracks the open-order stack for
/// orderly close-all, and raises change events.
///
/// <para>Android specifics are replaced per the gap guide: <c>Uri</c> →
/// <see cref="LocationUri"/>, broadcast intents → plain C# events
/// (<see cref="LocationAdded"/>/<see cref="LocationRemoved"/>/<see cref="LocationChanged"/>),
/// <c>Context</c>/DI → constructor injection, <c>org.json</c> →
/// <see cref="System.Text.Json"/>. Auto-close <em>timers</em> belong to the service
/// layer (Phase E); the hooks they need (open registry, closing order, last
/// activity) live here.</para>
/// </summary>
public sealed class LocationsManager
{
    private sealed class LocationInfo(ILocation location, bool store)
    {
        public readonly ILocation Location = location;
        public bool Store = store;
    }

    private readonly ISettings _settings;
    private readonly Dictionary<string, ILocationFactory> _factories = new();
    private readonly List<LocationInfo> _currentLocations = new();
    private readonly List<string> _openedStack = new();
    private readonly object _lock = new();

    public event EventHandler<LocationEventArgs>? LocationAdded;
    public event EventHandler<LocationEventArgs>? LocationRemoved;
    public event EventHandler<LocationEventArgs>? LocationChanged;

    /// <summary>
    /// Default protection-key provider assigned to every location this manager
    /// creates (for encrypting saved passwords in the settings blob). Supplied by
    /// the platform host from secret storage; null leaves protected fields in the
    /// clear.
    /// </summary>
    public IProtectionKeyProvider? ProtectionKeyProvider { get; set; }

    public LocationsManager(ISettings settings)
    {
        _settings = settings;
    }

    /// <summary>Registers Device + EncFS factories (the ones that live in Core).</summary>
    public LocationsManager RegisterCoreFactories()
    {
        RegisterFactory(new DeviceLocationFactory());
        RegisterFactory(new EncFsLocationFactory());
        return this;
    }

    public void RegisterFactory(ILocationFactory factory) => _factories[factory.Scheme] = factory;

    public ISettings Settings => _settings;

    // --- creation -------------------------------------------------------

    /// <summary>Dispatches to the factory for the URI scheme; null if unknown.</summary>
    public ILocation? CreateLocationFromUri(LocationUri uri)
    {
        if (!_factories.TryGetValue(uri.Scheme, out var f)) return null;
        var loc = f.CreateFromUri(this, _settings, uri);
        if (loc is LocationBase lb && lb.ProtectionKeyProvider == null)
            lb.ProtectionKeyProvider = ProtectionKeyProvider;
        return loc;
    }

    private string? GetLocationIdFromUri(LocationUri uri)
        => _factories.TryGetValue(uri.Scheme, out var f) ? f.GetLocationId(this, uri) : null;

    /// <summary>Resolves the base location referenced by an EDS URI's <c>location</c> query param.</summary>
    public ILocation ResolveBaseLocation(LocationUri uri)
    {
        var baseUriString = uri.GetQueryParameter("location")
            ?? throw new ArgumentException("Location URI has no base 'location' parameter: " + uri);
        return GetLocation(LocationUri.Parse(baseUriString));
    }

    /// <summary>
    /// Returns a location for the URI: an existing registered instance (copied and
    /// re-pointed at the URI's path) when one matches, otherwise a freshly created
    /// one, which is added to the registry unstored. Mirrors <c>getLocation</c>.
    /// </summary>
    public ILocation GetLocation(LocationUri uri)
    {
        var id = GetLocationIdFromUri(uri);
        if (id != null)
        {
            var prev = FindExistingLocation(id);
            if (prev != null)
            {
                var copy = prev.Copy();
                copy.LoadFromUri(uri);
                return copy;
            }
        }
        var loc = CreateLocationFromUri(uri)
            ?? throw new ArgumentException("Unsupported location uri: " + uri);
        if (FindExistingLocation(loc.GetId()) == null)
            AddNewLocation(loc, false);
        return loc;
    }

    public ILocation GetLocation(string uriString) => GetLocation(LocationUri.Parse(uriString));

    // --- registry -------------------------------------------------------

    public void AddNewLocation(ILocation loc, bool store)
    {
        lock (_lock)
        {
            _currentLocations.Add(new LocationInfo(loc, store));
            if (store) SaveCurrentLocationLinks();
        }
        LocationAdded?.Invoke(this, new LocationEventArgs(loc));
    }

    public void RemoveLocation(ILocation loc)
    {
        bool removed = false;
        lock (_lock)
        {
            var li = FindInfo(loc.GetId());
            if (li != null)
            {
                _currentLocations.Remove(li);
                if (li.Store) SaveCurrentLocationLinks();
                removed = true;
            }
        }
        if (removed) LocationRemoved?.Invoke(this, new LocationEventArgs(loc));
    }

    public void ReplaceLocation(ILocation oldLoc, ILocation newLoc, bool store)
    {
        RemoveLocation(oldLoc);
        AddNewLocation(newLoc, store);
    }

    public ILocation? FindExistingLocation(string locationId)
    {
        lock (_lock) return FindInfo(locationId)?.Location;
    }

    public bool IsStoredLocation(string locationId)
    {
        lock (_lock)
        {
            var li = FindInfo(locationId);
            return li is { Store: true };
        }
    }

    private LocationInfo? FindInfo(string locationId)
    {
        foreach (var li in _currentLocations)
            if (li.Location.GetId() == locationId) return li;
        return null;
    }

    public IReadOnlyList<ILocation> GetLoadedLocations(bool onlyVisible)
    {
        lock (_lock)
        {
            var res = new List<ILocation>();
            foreach (var li in _currentLocations)
                if (!onlyVisible || li.Location.GetExternalSettings().IsVisibleToUser)
                    res.Add(li.Location);
            return res;
        }
    }

    public string GenNewLocationId()
    {
        while (true)
        {
            var id = SimpleCrypto.CalcStringMd5(
                DateTimeOffset.UtcNow.Ticks.ToString() + Random.Shared.NextInt64().ToString());
            if (FindExistingLocation(id) == null) return id;
        }
    }

    /// <summary>Notify listeners that a location's mutable state changed (open/close/path).</summary>
    public void NotifyLocationChanged(ILocation loc)
        => LocationChanged?.Invoke(this, new LocationEventArgs(loc));

    // --- persistence ----------------------------------------------------

    public void LoadStoredLocations()
    {
        lock (_lock)
        {
            foreach (var uri in GetStoredLocationUris())
            {
                try
                {
                    var loc = CreateLocationFromUri(uri)
                        ?? throw new ArgumentException("Unsupported location uri: " + uri);
                    _currentLocations.Add(new LocationInfo(loc, true));
                }
                catch
                {
                    // skip malformed / unsupported stored entries
                }
            }
        }
    }

    public IReadOnlyList<LocationUri> GetStoredLocationUris()
    {
        var res = new List<LocationUri>();
        var raw = _settings.GetStoredLocations();
        if (string.IsNullOrEmpty(raw)) return res;
        string[]? list;
        try { list = JsonSerializer.Deserialize<string[]>(raw); }
        catch { return res; }
        if (list == null) return res;
        foreach (var s in list)
            if (LocationUri.TryParse(s, out var u) && u != null) res.Add(u);
        return res;
    }

    public void SaveCurrentLocationLinks()
    {
        List<string> links = new();
        lock (_lock)
        {
            foreach (var li in _currentLocations)
                if (li.Store) links.Add(li.Location.GetLocationUri().ToString());
        }
        _settings.SetStoredLocations(JsonSerializer.Serialize(links));
    }

    // --- open registry / closing ---------------------------------------

    public void RegOpenedLocation(ILocation loc)
    {
        lock (_lock) _openedStack.Add(loc.GetId());
    }

    public void UnregOpenedLocation(ILocation loc)
    {
        lock (_lock)
        {
            var id = loc.GetId();
            _openedStack.RemoveAll(x => x == id);
        }
    }

    /// <summary>Loaded openable locations in reverse open order (close newest first).</summary>
    public IReadOnlyList<ILocation> GetLocationsClosingOrder()
    {
        lock (_lock)
        {
            var res = new List<ILocation>();
            for (int i = _openedStack.Count - 1; i >= 0; i--)
            {
                var loc = FindInfo(_openedStack[i])?.Location;
                if (loc != null) res.Add(loc);
            }
            return res;
        }
    }

    public bool HasOpenLocations()
    {
        foreach (var loc in GetLoadedLocations(false))
            if (loc is IOpenableLocation ol && ol.IsOpen()) return true;
        return false;
    }

    public void CloseLocation(ILocation loc, bool force)
    {
        loc.CloseFileSystem(force);
        if (loc is IOpenableLocation ol)
        {
            ol.Close(force);
            UnregOpenedLocation(loc);
        }
    }

    public void CloseAllLocations(bool force)
    {
        foreach (var loc in GetLocationsClosingOrder())
            TryClose(loc, force);
        foreach (var loc in GetLoadedLocations(false))
            TryClose(loc, force);
    }

    private void TryClose(ILocation loc, bool force)
    {
        try { CloseLocation(loc, force); }
        catch when (force) { /* swallow */ }
    }
}
