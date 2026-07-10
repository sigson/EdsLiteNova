using System.Text.Json;

namespace Eds.Core.Settings;

/// <summary>
/// A file-backed <see cref="ISettings"/>: the string key/value map (the stored
/// location list and per-location settings blobs) is persisted to a JSON file, so
/// registered locations survive process restarts without a platform secret store.
/// A good default for the console host / desktop; the MAUI app would instead back
/// <see cref="InMemorySettings.Load"/>/<see cref="InMemorySettings.Store"/> with
/// <c>Preferences</c> + <c>SecureStorage</c>.
///
/// <para>Writes are flushed on every change (settings are tiny and change rarely).
/// The app-level prefs <see cref="NeverSaveHistory"/> /
/// <see cref="MaxContainerInactivityTime"/> are persisted here too.</para>
/// </summary>
public sealed class JsonFileSettings : InMemorySettings
{
    private readonly string _path;
    private readonly Dictionary<string, string> _data;
    private readonly object _lock = new();

    public JsonFileSettings(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _data = LoadFile(path);
    }

    public string FilePath => _path;

    // App-level prefs persisted alongside the string map.
    public override bool NeverSaveHistory
    {
        get => Load("app.never_save_history") == "1";
        set => Store("app.never_save_history", value ? "1" : null);
    }

    public override int MaxContainerInactivityTime
    {
        get => int.TryParse(Load("app.max_inactivity"), out var v) ? v : 0;
        set => Store("app.max_inactivity", value > 0 ? value.ToString() : null);
    }

    protected override string? Load(string key)
    {
        lock (_lock) return _data.TryGetValue(key, out var v) ? v : null;
    }

    protected override void Store(string key, string? value)
    {
        lock (_lock)
        {
            if (value == null) _data.Remove(key);
            else _data[key] = value;
            SaveFile();
        }
    }

    private static Dictionary<string, string> LoadFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch
        {
            // Corrupt/unreadable settings file: start empty rather than crash.
        }
        return new();
    }

    private void SaveFile()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data));
    }
}
