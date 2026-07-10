using Eds.Core.Fs.EncFs;
using Eds.Core.Fs.Vfs;
using Eds.Core.Settings;

namespace Eds.Core.Locations;

/// <summary>
/// An EncFS volume presented as a location. Port of the platform-independent
/// essence of <c>EncFsLocationBase</c>: the base location's current path points at
/// the EncFS root directory (holding the encrypted files with encrypted names);
/// opening derives the master key from the password and builds an
/// <see cref="EncFsFs"/> over that directory. Here the opened resource <em>is</em>
/// the mounted filesystem, so "open" and "mount" coincide.
/// </summary>
public sealed class EncFsLocation : EdsLocationBase
{
    public const string UriSchemeConst = "encfs";

    private EncFsFs? _encFs;

    public EncFsLocation(ISettings settings, ILocation baseLocation) : base(settings, baseLocation) { }

    private EncFsLocation(EncFsLocation sibling) : base(sibling.GlobalSettings, sibling.BaseLocation.Copy())
    {
        CurrentPathString = sibling.CurrentPathString;
    }

    public static string GetLocationId(ILocation baseLocation)
        => Crypto.SimpleCrypto.CalcStringMd5(baseLocation.GetLocationUri().ToString());

    protected override string UriScheme => UriSchemeConst;

    public override void Open()
    {
        if (IsOpen()) return;
        var pass = GetFinalPassword();
        try
        {
            var rootRealPath = BaseLocation.GetCurrentPath();
            _encFs = new EncFsFs(rootRealPath, pass);
        }
        finally
        {
            Array.Clear(pass);
        }
    }

    public override bool IsOpen() => _encFs != null;

    protected override IFileSystem CreateInnerFs(bool readOnly)
        => _encFs ?? throw new InvalidOperationException("EncFS volume is not open");

    public override void Close(bool force)
    {
        base.Close(force); // closes mounted fs (== _encFs) + clears password
        if (_encFs != null)
        {
            try { _encFs.Close(force); }
            catch (Exception) when (force) { /* swallow */ }
            _encFs = null;
        }
    }

    public override ILocation Copy() => new EncFsLocation(this);

    protected override ExternalSettingsBase CreateExternalSettings() => new EncFsExternalSettings();

    private sealed class EncFsExternalSettings : EdsExternalSettings { }
}
