using Eds.Core.Locations;
using Eds.Core.Settings;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>File-backed settings: values and registered locations survive a restart.</summary>
public class SettingsTests
{
    [Fact]
    public void JsonFileSettings_Persists_And_Reloads_Values()
    {
        string path = NewFilePath();
        try
        {
            var s1 = new JsonFileSettings(path);
            s1.SetStoredLocations("[\"file:/?root=%2Ftmp\"]");
            s1.SetLocationSettingsString("id1", "{\"title\":\"X\"}");
            s1.SetLocationSettingsString("id2", "temp");
            s1.SetLocationSettingsString("id2", null); // deletion

            var s2 = new JsonFileSettings(path); // "restart"
            Assert.Equal("[\"file:/?root=%2Ftmp\"]", s2.GetStoredLocations());
            Assert.Equal("{\"title\":\"X\"}", s2.GetLocationSettingsString("id1"));
            Assert.Null(s2.GetLocationSettingsString("id2"));
        }
        finally { TryDeleteParent(path); }
    }

    [Fact]
    public void Corrupt_Settings_File_Starts_Empty()
    {
        string path = NewFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not valid json ");
        try
        {
            var s = new JsonFileSettings(path);
            Assert.Null(s.GetStoredLocations()); // didn't throw, just empty
            s.SetStoredLocations("[]");          // and is usable
            Assert.Equal("[]", new JsonFileSettings(path).GetStoredLocations());
        }
        finally { TryDeleteParent(path); }
    }

    [Fact]
    public void Registered_Location_Survives_With_JsonFileSettings()
    {
        string path = NewFilePath();
        string dir = NewDir();
        try
        {
            var s1 = new JsonFileSettings(path);
            var mgr1 = new LocationsManager(s1).RegisterCoreFactories();
            var dev = new DeviceLocation(s1, dir);
            mgr1.AddNewLocation(dev, store: true);
            string id = dev.GetId();

            // Fresh settings + manager over the same file.
            var s2 = new JsonFileSettings(path);
            var mgr2 = new LocationsManager(s2).RegisterCoreFactories();
            mgr2.LoadStoredLocations();

            Assert.NotNull(mgr2.FindExistingLocation(id));
        }
        finally { TryDeleteParent(path); TryDeleteDir(dir); }
    }

    // ---- helpers -------------------------------------------------------

    private static string NewFilePath()
        => Path.Combine(Path.GetTempPath(), $"eds_set_{Guid.NewGuid():N}", "settings.json");

    private static string NewDir()
    {
        string d = Path.Combine(Path.GetTempPath(), $"eds_setd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static void TryDeleteParent(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { /* ignore */ }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }
}
