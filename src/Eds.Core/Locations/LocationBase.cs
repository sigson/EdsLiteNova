using System.Text;
using System.Text.Json.Nodes;
using Eds.Core.Crypto;
using Eds.Core.Fs.Vfs;
using Eds.Core.Settings;

namespace Eds.Core.Locations;

/// <summary>
/// Abstract base for locations. Faithful, de-Androidised port of
/// <c>locations.LocationBase</c>: it owns the current-path string and the
/// external-settings load/save cycle (a JSON blob keyed by the location id in
/// <see cref="ISettings"/>), including protected fields.
///
/// <para>Protected-field encoding differs from the Android original only in being
/// self-describing: values carry an <c>h:</c> (plain hex) or <c>e:</c> (encrypted)
/// prefix, so a reader can tell how a field was written without depending on
/// whether a protection key happened to be present. Compatibility with old
/// Android blobs is not required (gap guide §2.5).</para>
/// </summary>
public abstract class LocationBase : ILocation
{
    protected readonly ISettings GlobalSettings;
    protected string? CurrentPathString;
    private IExternalSettings? _externalSettings;

    /// <summary>
    /// Supplies the key that protects sensitive settings fields (a saved
    /// password). Assigned by the host or the <see cref="LocationsManager"/>
    /// (from platform secret storage); null means protected fields are stored in
    /// the clear. Must be set before the external settings are first accessed.
    /// </summary>
    public IProtectionKeyProvider? ProtectionKeyProvider { get; set; }

    protected LocationBase(ISettings settings)
    {
        GlobalSettings = settings;
    }

    public abstract string GetId();
    public abstract IFileSystem GetFileSystem();
    public abstract LocationUri GetLocationUri();
    public abstract ILocation Copy();

    public virtual IPath GetCurrentPath()
        => CurrentPathString == null ? GetFileSystem().GetRootPath() : GetFileSystem().GetPath(CurrentPathString);

    public virtual void SetCurrentPath(IPath? path)
        => CurrentPathString = path?.PathString;

    public virtual void LoadFromUri(LocationUri uri) { }

    public virtual string GetTitle() => GetExternalSettings().Title ?? "";

    public virtual bool IsReadOnly() => false;
    public virtual bool IsEncrypted() => false;
    public virtual bool IsDirectlyAccessible() => false;

    public virtual IExternalSettings GetExternalSettings()
        => _externalSettings ??= LoadExternalSettings();

    public virtual void SaveExternalSettings()
        => GlobalSettings.SetLocationSettingsString(GetId(), ((ExternalSettingsBase)GetExternalSettings()).Save());

    public virtual void CloseFileSystem(bool force) { }
    public virtual bool IsFileSystemOpen() => false;

    /// <summary>Creates the external-settings instance for this location type and loads it from storage.</summary>
    protected virtual IExternalSettings LoadExternalSettings()
    {
        var res = CreateExternalSettings();
        res.SetProtectionKeyProvider(ProtectionKeyProvider);
        res.Load(GlobalSettings.GetLocationSettingsString(GetId()));
        return res;
    }

    /// <summary>Factory for the concrete external-settings type. Overridden per location.</summary>
    protected abstract ExternalSettingsBase CreateExternalSettings();

    // ---------------------------------------------------------------------

    /// <summary>
    /// Base of the per-location settings blob. Concrete locations extend this to add
    /// their own keys (open-read-only, saved password, container format, …).
    /// </summary>
    public abstract class ExternalSettingsBase : IExternalSettings
    {
        private IProtectionKeyProvider? _protectionKeyProvider;

        public string? Title { get; set; }
        public bool IsVisibleToUser { get; set; }
        public bool UseExternalFileManager { get; set; } = true;

        public void SetProtectionKeyProvider(IProtectionKeyProvider? p) => _protectionKeyProvider = p;

        public string Save()
        {
            var jo = new JsonObject();
            SaveToJson(jo);
            return jo.ToJsonString();
        }

        public void Load(string? data)
        {
            JsonObject jo;
            try { jo = (data == null ? null : JsonNode.Parse(data) as JsonObject) ?? new JsonObject(); }
            catch { jo = new JsonObject(); }
            try { LoadFromJson(jo); } catch { /* keep defaults */ }
        }

        public override string ToString() => Save();

        protected virtual void SaveToJson(JsonObject jo)
        {
            if (Title != null) jo["title"] = Title;
            jo["visible_to_user"] = IsVisibleToUser;
            jo["use_ext_file_manager"] = UseExternalFileManager;
        }

        protected virtual void LoadFromJson(JsonObject jo)
        {
            Title = (string?)jo["title"];
            IsVisibleToUser = GetBool(jo, "visible_to_user", false);
            UseExternalFileManager = GetBool(jo, "use_ext_file_manager", true);
        }

        protected static bool GetBool(JsonObject jo, string key, bool def)
            => jo[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

        protected static int GetInt(JsonObject jo, string key, int def)
            => jo[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : def;

        protected void StoreProtectedField(JsonObject jo, string key, string? data)
        {
            if (data != null) jo[key] = EncryptAndEncode(Encoding.UTF8.GetBytes(data));
        }

        protected string EncryptAndEncode(byte[] data)
        {
            var pk = _protectionKeyProvider?.GetProtectionKey();
            if (pk == null) return "h:" + SimpleCrypto.ToHexString(data);
            try { return "e:" + SimpleCrypto.Encrypt(pk.AsSpan(), data); }
            finally { pk.Dispose(); }
        }

        protected string? LoadProtectedString(JsonObject jo, string key)
        {
            var d = LoadProtectedData(jo, key);
            return d == null ? null : Encoding.UTF8.GetString(d);
        }

        protected byte[]? LoadProtectedData(JsonObject jo, string key)
        {
            var s = (string?)jo[key];
            if (s == null) return null;
            try { return DecodeAndDecrypt(s); }
            catch { return null; }
        }

        private byte[]? DecodeAndDecrypt(string data)
        {
            if (data.StartsWith("h:", StringComparison.Ordinal))
                return SimpleCrypto.FromHexString(data[2..]);
            if (data.StartsWith("e:", StringComparison.Ordinal))
            {
                var pk = _protectionKeyProvider?.GetProtectionKey();
                if (pk == null) return null;
                try { return SimpleCrypto.Decrypt(pk.AsSpan(), data[2..]); }
                finally { pk.Dispose(); }
            }
            return Encoding.UTF8.GetBytes(data); // legacy/plain
        }
    }
}
