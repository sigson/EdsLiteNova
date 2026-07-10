using System.Collections.Concurrent;

namespace Eds.Core.Settings;

/// <summary>
/// A simple in-memory <see cref="ISettings"/>. Used by tests and the console host,
/// and usable as a base for a persistent implementation: override
/// <see cref="Load"/>/<see cref="Store"/> to back the string map with a real
/// key/value store (MAUI <c>Preferences</c>/<c>SecureStorage</c>, a JSON file,
/// DPAPI, etc.). All keys/values are plain strings, matching the original
/// <c>SharedPreferences</c> string model.
/// </summary>
public class InMemorySettings : ISettings
{
    private const string KeyStoredLocations = "stored_locations";
    private const string LocationSettingsPrefix = "location_settings.";

    private readonly ConcurrentDictionary<string, string> _map = new();

    public virtual bool NeverSaveHistory { get; set; }
    public virtual int MaxContainerInactivityTime { get; set; }

    public string? GetStoredLocations() => Load(KeyStoredLocations);

    public void SetStoredLocations(string? locations) => Store(KeyStoredLocations, locations);

    public string? GetLocationSettingsString(string locationId)
        => Load(LocationSettingsPrefix + locationId);

    public void SetLocationSettingsString(string locationId, string? data)
        => Store(LocationSettingsPrefix + locationId, data);

    /// <summary>Reads a raw string value, or null if unset. Override for persistence.</summary>
    protected virtual string? Load(string key)
        => _map.TryGetValue(key, out var v) ? v : null;

    /// <summary>Writes (or, when <paramref name="value"/> is null, clears) a raw string value.</summary>
    protected virtual void Store(string key, string? value)
    {
        if (value == null) _map.TryRemove(key, out _);
        else _map[key] = value;
    }
}
