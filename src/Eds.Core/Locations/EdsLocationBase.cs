using System.Text.Json.Nodes;
using Eds.Core.Containers;
using Eds.Core.Crypto;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;
using Eds.Core.Settings;

namespace Eds.Core.Locations;

/// <summary>
/// Base for password-openable locations. Port of <c>locations.OMLocationBase</c>:
/// it manages the transient password (a <see cref="SecureBuffer"/>), the KDF
/// iteration override, the read-only flag and the opening progress reporter, and
/// it extends the settings blob with an optionally-saved password and custom KDF
/// iteration count.
/// </summary>
public abstract class OpenableLocationBase : LocationBase, IOpenableLocation
{
    private SecureBuffer? _password;
    private IReadOnlyList<Func<Stream>>? _keyfiles;
    protected int NumKdfIterations;
    protected bool OpenReadOnly;
    protected IContainerOpeningProgressReporter? OpeningReporter;

    protected OpenableLocationBase(ISettings settings) : base(settings) { }

    public virtual void SetPassword(SecureBuffer? password)
    {
        if (_password != null && !ReferenceEquals(_password, password))
            _password.Dispose();
        _password = password;
    }

    public void SetKeyfiles(IReadOnlyList<Func<Stream>>? keyfiles) => _keyfiles = keyfiles;

    /// <summary>Keyfiles to mix into the password on open (null if none).</summary>
    protected IReadOnlyList<Func<Stream>>? Keyfiles => _keyfiles;

    public virtual bool HasPassword() => false;

    public virtual bool RequirePassword()
        => HasPassword() && !OpenableSettings.HasSavedPassword;

    public virtual bool HasCustomKdfIterations() => false;

    public virtual bool RequireCustomKdfIterations()
        => HasCustomKdfIterations() && OpenableSettings.CustomKdfIterations < 0;

    public void SetNumKdfIterations(int num) => NumKdfIterations = num;

    public void SetOpenReadOnly(bool readOnly) => OpenReadOnly = readOnly;

    public void SetOpeningProgressReporter(IContainerOpeningProgressReporter? reporter)
        => OpeningReporter = reporter;

    public override bool IsReadOnly() => OpenReadOnly;

    public abstract bool IsOpen();
    public abstract void Open();

    public virtual void Close(bool force)
    {
        CloseFileSystem(force);
        var p = _password;
        if (p != null) { p.Dispose(); _password = null; }
    }

    protected SecureBuffer? Password => _password;

    protected OpenableExternalSettings OpenableSettings => (OpenableExternalSettings)GetExternalSettings();

    /// <summary>Live password if set, otherwise the saved one, otherwise empty.</summary>
    protected byte[] GetSelectedPassword()
    {
        var p = _password;
        if (p != null && p.Length > 0) return p.GetBytes();
        return OpenableSettings.GetSavedPassword() ?? Array.Empty<byte>();
    }

    protected int GetSelectedKdfIterations()
        => NumKdfIterations == 0 ? OpenableSettings.CustomKdfIterations : NumKdfIterations;

    protected virtual byte[] GetFinalPassword() => GetSelectedPassword();

    /// <summary>Settings blob for openables: adds the saved password and custom KDF iterations.</summary>
    public abstract class OpenableExternalSettings : ExternalSettingsBase
    {
        private string? _pass; // stored, protected form
        public int CustomKdfIterations { get; set; } = -1;

        public bool HasSavedPassword => !string.IsNullOrEmpty(_pass);

        public void SetSavedPassword(byte[]? password)
            => _pass = password == null ? null : EncryptAndEncode(password);

        public byte[]? GetSavedPassword()
            => _pass == null ? null : DecodeSavedPassword(_pass);

        private byte[]? DecodeSavedPassword(string stored)
        {
            var jo = new JsonObject { ["pass"] = stored };
            return LoadProtectedData(jo, "pass");
        }

        protected override void SaveToJson(JsonObject jo)
        {
            base.SaveToJson(jo);
            if (_pass != null) jo["pass"] = _pass;
            if (CustomKdfIterations >= 0)
                StoreProtectedField(jo, "custom_kdf_iterations", CustomKdfIterations.ToString());
        }

        protected override void LoadFromJson(JsonObject jo)
        {
            base.LoadFromJson(jo);
            _pass = (string?)jo["pass"];
            var iters = LoadProtectedString(jo, "custom_kdf_iterations");
            CustomKdfIterations = iters != null && int.TryParse(iters, out var n) ? n : -1;
        }
    }
}

/// <summary>
/// Base for encrypted (EDS) locations layered over a base location. Port of the
/// platform-independent parts of <c>EDSLocationBase</c>: it holds the base
/// location whose current path points at the encrypted store, lazily builds the
/// inner (mounted) filesystem once the location is opened, and adds the
/// open-read-only / auto-close-timeout settings. The id is the MD5 of the base
/// location URI, matching the original.
/// </summary>
public abstract class EdsLocationBase : OpenableLocationBase, IEdsLocation
{
    protected readonly ILocation BaseLocation;
    private IFileSystem? _mountedFs;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

    protected EdsLocationBase(ISettings settings, ILocation baseLocation) : base(settings)
    {
        BaseLocation = baseLocation;
    }

    public ILocation GetBaseLocation() => BaseLocation;

    public override string GetId() => SimpleCrypto.CalcStringMd5(BaseLocation.GetLocationUri().ToString());

    public override bool HasPassword() => true;
    public override bool IsEncrypted() => true;

    public bool ShouldOpenReadOnly() => EdsSettings.OpenReadOnly;
    public void SetOpenReadOnly2(bool val) => EdsSettings.OpenReadOnly = val;

    public int GetAutoCloseTimeout()
    {
        var t = EdsSettings.AutoCloseTimeout;
        return t > 0 ? t : GlobalSettings.MaxContainerInactivityTime;
    }
    public void SetAutoCloseTimeout(int seconds) => EdsSettings.AutoCloseTimeout = seconds;

    public DateTimeOffset GetLastActivityTime() => _lastActivity;

    public override bool IsReadOnly() => base.IsReadOnly() || ShouldOpenReadOnly();

    /// <summary>The scheme used in <see cref="GetLocationUri"/>.</summary>
    protected abstract string UriScheme { get; }

    public override IFileSystem GetFileSystem()
    {
        if (_mountedFs == null)
        {
            if (!IsOpen())
                throw new IOException("Cannot access closed container.");
            _mountedFs = CreateInnerFs(ShouldOpenReadOnly());
        }
        _lastActivity = DateTimeOffset.UtcNow;
        return _mountedFs;
    }

    /// <summary>Builds the mounted inner filesystem over the (already opened) store.</summary>
    protected abstract IFileSystem CreateInnerFs(bool readOnly);

    public override bool IsFileSystemOpen() => _mountedFs != null;

    public override void CloseFileSystem(bool force)
    {
        try { _mountedFs?.Close(force); }
        catch (Exception) when (force) { /* swallow on force */ }
        _mountedFs = null;
    }

    public override LocationUri GetLocationUri()
        => new LocationUri(UriScheme, CurrentPathString ?? "/")
            .WithQueryParameter(LocationParam, BaseLocation.GetLocationUri().ToString());

    public override void LoadFromUri(LocationUri uri)
    {
        var p = uri.Path;
        CurrentPathString = string.IsNullOrEmpty(p) || p == "/" ? null : p;
    }

    public override string GetTitle()
    {
        var t = GetExternalSettings().Title;
        if (!string.IsNullOrEmpty(t)) return t;
        // Fall back to the container file name (without extension) via the base location.
        try
        {
            var basePath = BaseLocation.GetCurrentPath();
            var name = new StringPathUtil(basePath.PathString).GetFileNameWithoutExtension();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch { /* ignore */ }
        return BaseLocation.GetLocationUri().ToString();
    }

    public override void SaveExternalSettings()
    {
        if (!GlobalSettings.NeverSaveHistory)
            base.SaveExternalSettings();
    }

    protected const string LocationParam = "location";

    protected EdsExternalSettings EdsSettings => (EdsExternalSettings)GetExternalSettings();

    /// <summary>Settings blob for EDS locations: adds open-read-only and auto-close timeout.</summary>
    public abstract class EdsExternalSettings : OpenableExternalSettings, IEdsExternalSettings
    {
        public bool OpenReadOnly { get; set; }
        public int AutoCloseTimeout { get; set; }

        protected override void SaveToJson(JsonObject jo)
        {
            base.SaveToJson(jo);
            jo["read_only"] = OpenReadOnly;
            if (AutoCloseTimeout > 0) jo["auto_close_timeout"] = AutoCloseTimeout;
        }

        protected override void LoadFromJson(JsonObject jo)
        {
            base.LoadFromJson(jo);
            OpenReadOnly = GetBool(jo, "read_only", false);
            AutoCloseTimeout = GetInt(jo, "auto_close_timeout", 0);
        }
    }
}

/// <summary>Marker for EDS external settings (open-read-only, auto-close).</summary>
public interface IEdsExternalSettings : IExternalSettings
{
    bool OpenReadOnly { get; set; }
    int AutoCloseTimeout { get; set; }
}
