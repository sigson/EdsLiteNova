using Eds.Core.Containers;
using Eds.Core.Crypto;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Locations;

/// <summary>
/// Supplies the key used to protect sensitive per-location settings (e.g. a saved
/// password). Mirrors <c>Location.ProtectionKeyProvider</c>. Returns null when no
/// key is configured, in which case protected fields are stored in the clear.
/// </summary>
public interface IProtectionKeyProvider
{
    SecureBuffer? GetProtectionKey();
}

/// <summary>
/// Per-location user settings persisted as a JSON blob (title, visibility, and —
/// for openable locations — the optionally-protected saved password / KDF count).
/// Mirrors <c>Location.ExternalSettings</c>. Serialization is handled by the
/// concrete <see cref="LocationBase.ExternalSettingsBase"/> classes.
/// </summary>
public interface IExternalSettings
{
    void SetProtectionKeyProvider(IProtectionKeyProvider? p);
    string? Title { get; set; }
    bool IsVisibleToUser { get; set; }
    bool UseExternalFileManager { get; set; }
}

/// <summary>
/// A place where files live: a folder on the device, a container file, or an
/// EncFS directory. Faithful port of the platform-independent parts of
/// <c>locations.Location</c> (Android <c>Uri</c>/<c>Intent</c>/<c>Parcelable</c>
/// members dropped; <c>System.Uri</c> replaced by <see cref="LocationUri"/>).
///
/// Opening an encrypted location (see <see cref="IOpenableLocation"/>) makes a
/// virtual <see cref="IFileSystem"/> available through <see cref="GetFileSystem"/>.
/// </summary>
public interface ILocation
{
    string GetTitle();

    /// <summary>Stable identifier used as the persistence key. Mirrors <c>getId()</c>.</summary>
    string GetId();

    /// <summary>The mounted filesystem. Throws / returns closed FS if not available.</summary>
    IFileSystem GetFileSystem();

    /// <summary>Current path within <see cref="GetFileSystem"/> (root if unset).</summary>
    IPath GetCurrentPath();
    void SetCurrentPath(IPath? path);

    LocationUri GetLocationUri();
    void LoadFromUri(LocationUri uri);

    /// <summary>Independent copy that shares no mutable open state.</summary>
    ILocation Copy();

    void CloseFileSystem(bool force);
    bool IsFileSystemOpen();

    bool IsReadOnly();
    bool IsEncrypted();

    /// <summary>True when the underlying store is a plain, directly-accessible device path.</summary>
    bool IsDirectlyAccessible();

    IExternalSettings GetExternalSettings();
    void SaveExternalSettings();
}

/// <summary>
/// A location that must be opened with a password before its filesystem is
/// available. Port of <c>locations.Openable</c> + the open/close behaviour of
/// <c>OMLocationBase</c>. Progress/cancellation flow through
/// <see cref="IContainerOpeningProgressReporter"/> (the KDF brute-force sweep).
/// </summary>
public interface IOpenableLocation : ILocation
{
    void SetPassword(SecureBuffer? password);
    bool HasPassword();
    bool RequirePassword();

    /// <summary>
    /// Optional keyfiles mixed into the password on open (TrueCrypt/VeraCrypt).
    /// Each factory yields a fresh readable stream of one keyfile's bytes.
    /// </summary>
    void SetKeyfiles(IReadOnlyList<Func<Stream>>? keyfiles);

    bool HasCustomKdfIterations();
    bool RequireCustomKdfIterations();
    void SetNumKdfIterations(int num);

    void SetOpenReadOnly(bool readOnly);

    bool IsOpen();
    void Open();
    void Close(bool force);

    void SetOpeningProgressReporter(IContainerOpeningProgressReporter? reporter);
}

/// <summary>
/// An encrypted (EDS) location layered over a <em>base</em> location whose current
/// path points at the encrypted store (a container file, or an EncFS root folder).
/// Port of the platform-independent parts of <c>locations.EDSLocation</c>.
/// </summary>
public interface IEdsLocation : IOpenableLocation
{
    /// <summary>The underlying base location (device folder holding the container/encfs root).</summary>
    ILocation GetBaseLocation();

    bool ShouldOpenReadOnly();
    void SetOpenReadOnly2(bool val);

    int GetAutoCloseTimeout();
    void SetAutoCloseTimeout(int seconds);

    /// <summary>Wall-clock time of the last filesystem activity (for auto-close).</summary>
    DateTimeOffset GetLastActivityTime();
}
