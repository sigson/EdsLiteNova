namespace Eds.Core.Settings;

/// <summary>
/// Platform-independent application settings. This is the lean core the
/// locations layer depends on — a faithful subset of the original
/// <c>settings.SettingsCommon</c> reduced to what <see cref="Eds.Core.Locations"/>
/// actually needs. Android-only members (<c>SharedPreferences</c>, widget infos,
/// external-file-manager intents, UI sort/theme constants, <c>Parcelable</c>) are
/// intentionally omitted; the MAUI/console host supplies a concrete
/// implementation (over <c>Preferences</c>/<c>SecureStorage</c> or a file), and
/// tests use <see cref="InMemorySettings"/>.
///
/// <para>Two kinds of state live here:
/// <list type="bullet">
/// <item>the <em>list of registered locations</em> — a JSON array of location
/// URI strings (<see cref="GetStoredLocations"/>/<see cref="SetStoredLocations"/>);</item>
/// <item>the <em>per-location settings blob</em> — one JSON string keyed by the
/// location id (<see cref="GetLocationSettingsString"/>/<see
/// cref="SetLocationSettingsString"/>), holding title/visibility and — for
/// openable locations — the optionally password-protected saved password and KDF
/// iterations.</item>
/// </list></para>
/// </summary>
public interface ISettings
{
    /// <summary>JSON array of stored location URI strings. Mirrors <c>getStoredLocations()</c>.</summary>
    string? GetStoredLocations();

    /// <summary>Persists the JSON array of stored location URI strings.</summary>
    void SetStoredLocations(string? locations);

    /// <summary>The per-location settings JSON blob, or null. Mirrors <c>getLocationSettingsString(id)</c>.</summary>
    string? GetLocationSettingsString(string locationId);

    /// <summary>Persists the per-location settings JSON blob.</summary>
    void SetLocationSettingsString(string locationId, string? data);

    /// <summary>
    /// When true, EDS locations must not persist their external settings (history
    /// off). Mirrors <c>neverSaveHistory()</c>.
    /// </summary>
    bool NeverSaveHistory { get; }

    /// <summary>
    /// Default auto-close inactivity timeout for opened containers, in seconds
    /// (0 = never). Mirrors <c>getMaxContainerInactivityTime()</c>. Consumed by the
    /// service layer (Phase E); surfaced here so a location can fall back to it.
    /// </summary>
    int MaxContainerInactivityTime { get; }
}
