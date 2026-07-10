using System.Text.Json.Nodes;
using Eds.Core.Crypto;
using Eds.Core.Exceptions;
using Eds.Core.Fs.Fat;
using Eds.Core.Fs.Vfs;
using Eds.Core.Locations;
using Eds.Core.Settings;

namespace Eds.Core.Containers.Locations;

/// <summary>
/// A TrueCrypt/VeraCrypt/LUKS container presented as a location. This is the
/// keystone of Phase D: it connects the already-ported pieces — the base
/// <see cref="ILocation"/> (a device folder holding the container file), the
/// container reader (<see cref="EdsContainer"/>), the transparent cached volume
/// (<see cref="EncryptedFileWithCache"/>) and the inner filesystem
/// (<see cref="FatVfs"/>) — into a single openable location. Port of the
/// platform-independent essence of <c>ContainerBasedLocation</c>.
///
/// <para>Lives in <c>Eds.Core.Containers</c> (which references Core) so that Core's
/// <see cref="LocationsManager"/> stays free of a Containers dependency; the host
/// registers <see cref="ContainerLocationFactory"/> at start-up.</para>
/// </summary>
public sealed class ContainerLocation : EdsLocationBase
{
    public const string UriSchemeConst = "eds-container";
    public const int MaxPasswordLength = 64;

    private EdsContainer? _container;
    private EncryptedFileWithCache? _volume;

    public ContainerLocation(ISettings settings, ILocation baseLocation) : base(settings, baseLocation) { }

    private ContainerLocation(ContainerLocation sibling) : base(sibling.GlobalSettings, sibling.BaseLocation.Copy())
    {
        CurrentPathString = sibling.CurrentPathString;
    }

    public static string GetLocationId(ILocation baseLocation)
        => SimpleCrypto.CalcStringMd5(baseLocation.GetLocationUri().ToString());

    protected override string UriScheme => UriSchemeConst;

    public override void LoadFromUri(LocationUri uri)
    {
        // A container's own current path is a path inside the mounted FAT.
        CurrentPathString = uri.Path == "/" ? null : uri.Path;
    }

    public override bool HasCustomKdfIterations() => true;

    public override void Open()
    {
        if (IsOpen()) return;
        bool readOnly = ShouldOpenReadOnly();
        var baseIo = BaseLocation.GetCurrentPath().GetFile()
            .GetRandomAccessIO(readOnly ? FileAccessMode.Read : FileAccessMode.ReadWrite);

        var container = new EdsContainer(baseIo);
        try
        {
            var pass = GetFinalPassword();
            int pim = Math.Max(0, GetSelectedKdfIterations());
            var openOptions = new ContainerOpenOptions
            {
                Reporter = OpeningReporter,
                Pim = pim,
                Keyfiles = EffectiveKeyfiles(),
            };
            bool opened;
            try { opened = container.Open(pass, openOptions); }
            finally { Array.Clear(pass); }

            if (!opened) throw new WrongPasswordException();
            _container = container;
        }
        catch
        {
            container.Dispose(); // also disposes baseIo
            throw;
        }
    }

    public override bool IsOpen() => _container != null;

    /// <summary>
    /// Keyfiles to open with: the transient set (from
    /// <see cref="IOpenableLocation.SetKeyfiles"/>) if present, otherwise the paths
    /// remembered in this location's settings.
    /// </summary>
    private IReadOnlyList<Func<Stream>>? EffectiveKeyfiles()
    {
        if (Keyfiles != null) return Keyfiles;
        var paths = ((ContainerExternalSettings)GetExternalSettings()).KeyfilePaths;
        if (paths.Count == 0) return null;
        var list = new List<Func<Stream>>(paths.Count);
        foreach (var p in paths) list.Add(global::Eds.Core.Containers.Keyfiles.FromFile(p));
        return list;
    }

    protected override IFileSystem CreateInnerFs(bool readOnly)
    {
        if (_container == null) throw new InvalidOperationException("Container is not open");
        _volume = _container.GetCachedEncryptedVolume();
        var fat = FatFileSystem.Mount(_volume);
        return new FatVfs(fat, writable: !readOnly);
    }

    /// <summary>
    /// Re-keys the container header with a new password (and optional new keyfiles /
    /// PIM), keeping the master key. Requires the container to be open read-write.
    /// The remembered keyfile paths / PIM in this location's settings are not
    /// changed here — a caller that wants the new secrets to persist should update
    /// the external settings and save.
    /// </summary>
    public void ChangePassword(byte[] newPassword, IReadOnlyList<Func<Stream>>? newKeyfiles = null, int newPim = 0)
    {
        if (_container == null) throw new InvalidOperationException("Container is not open");
        _container.ChangePassword(newPassword, new ContainerOpenOptions { Keyfiles = newKeyfiles, Pim = newPim });
    }

    public override void Close(bool force)
    {
        base.Close(force); // closes mounted fs + clears password
        try
        {
            _volume?.Dispose();
            _container?.Dispose();
        }
        catch (Exception) when (force) { /* swallow */ }
        finally
        {
            _volume = null;
            _container = null;
        }
    }

    public override ILocation Copy() => new ContainerLocation(this);

    protected override byte[] GetFinalPassword()
    {
        var pass = base.GetFinalPassword();
        if (pass.Length > MaxPasswordLength)
        {
            var trimmed = new byte[MaxPasswordLength];
            Array.Copy(pass, trimmed, MaxPasswordLength);
            Array.Clear(pass);
            return trimmed;
        }
        return pass;
    }

    protected override ExternalSettingsBase CreateExternalSettings() => new ContainerExternalSettings();

    /// <summary>
    /// Container settings blob: remembers the format/cipher/hash chosen for the
    /// volume so the UI can display them (and a future optimisation can skip the
    /// algorithm sweep). Mirrors <c>ContainerBasedLocation.ExternalSettings</c>.
    /// </summary>
    public sealed class ContainerExternalSettings : EdsExternalSettings
    {
        public string? ContainerFormatName { get; set; }
        public string? EncEngineName { get; set; }
        public string? HashFuncName { get; set; }

        /// <summary>
        /// Keyfile locations remembered for this container (paths only — the
        /// keyfile <em>content</em> is the secret and is never stored). Mirrors
        /// VeraCrypt remembering the keyfile list per volume.
        /// </summary>
        public IReadOnlyList<string> KeyfilePaths { get; set; } = Array.Empty<string>();

        protected override void SaveToJson(JsonObject jo)
        {
            base.SaveToJson(jo);
            if (ContainerFormatName != null) jo["container_format"] = ContainerFormatName;
            if (EncEngineName != null) jo["encryption_engine"] = EncEngineName;
            if (HashFuncName != null) jo["hash_func"] = HashFuncName;
            if (KeyfilePaths.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var p in KeyfilePaths) arr.Add(p);
                jo["keyfiles"] = arr;
            }
        }

        protected override void LoadFromJson(JsonObject jo)
        {
            base.LoadFromJson(jo);
            ContainerFormatName = (string?)jo["container_format"];
            EncEngineName = (string?)jo["encryption_engine"];
            HashFuncName = (string?)jo["hash_func"];
            if (jo["keyfiles"] is JsonArray arr)
            {
                var list = new List<string>(arr.Count);
                foreach (var n in arr)
                {
                    var s = (string?)n;
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                KeyfilePaths = list;
            }
            else KeyfilePaths = Array.Empty<string>();
        }
    }
}
