using Eds.Core.Containers;
using Eds.Core.Containers.Locations;
using Eds.Core.Crypto;
using Eds.Core.Fs.Vfs;
using Eds.Core.Locations;
using Eds.Core.Services;
using Eds.Core.Settings;

namespace Eds.Core.App;

/// <summary>
/// The platform-independent application layer that sits above the core stack and
/// below the UI. It wires together the pieces built in Phases D/E/G — the
/// <see cref="LocationsManager"/> (registry + persistence), the
/// <see cref="IFileOperationsService"/> (copy/move/delete/wipe queue) and the
/// <see cref="AutoCloseService"/> (idle lock) — behind one small, testable API so
/// the MAUI layer (Phase F) can be a thin binding surface rather than re-deriving
/// this orchestration in each ViewModel.
///
/// <para>It lives in <c>Eds.Core.Containers</c> because it needs to build container
/// locations; everything it exposes is platform-neutral. The blocking, KDF-heavy
/// <see cref="OpenAsync"/> runs off the caller's thread so a UI stays responsive.</para>
/// </summary>
public sealed class EdsAppController
{
    private readonly ISettings _settings;
    private readonly LocationsManager _locations;
    private readonly IFileOperationsService _fileOps;
    private readonly AutoCloseService _autoClose;
    private readonly FileLister _lister = new();
    private readonly TempFileManager _tempFiles = new();
    private readonly IExternalFileOpener? _externalOpener;

    // Simple cross-page clipboard for copy/cut/paste between folders and locations.
    private readonly List<IPath> _clipboard = new();
    private bool _clipboardCut;

    public EdsAppController(
        ISettings settings,
        IFileOperationsService? fileOperations = null,
        ISystemClock? clock = null,
        IExternalFileOpener? externalOpener = null)
    {
        _settings = settings;
        _locations = new LocationsManager(settings).RegisterCoreFactories();
        _locations.RegisterFactory(new ContainerLocationFactory());
        _fileOps = fileOperations ?? new FileOperationsService();
        _autoClose = new AutoCloseService(_locations, clock);
        _externalOpener = externalOpener;
    }

    public LocationsManager Locations => _locations;
    public IFileOperationsService FileOperations => _fileOps;
    public AutoCloseService AutoClose => _autoClose;
    public ISettings Settings => _settings;

    /// <summary>Optional protection-key provider for saved passwords (platform secret store).</summary>
    public IProtectionKeyProvider? ProtectionKeyProvider
    {
        get => _locations.ProtectionKeyProvider;
        set => _locations.ProtectionKeyProvider = value;
    }

    // ---- registry ------------------------------------------------------

    /// <summary>Loads the persisted list of locations (call once at startup).</summary>
    public void LoadStoredLocations() => _locations.LoadStoredLocations();

    public IReadOnlyList<ILocation> GetLocations(bool onlyVisible = true)
        => _locations.GetLoadedLocations(onlyVisible);

    /// <summary>
    /// The locations the user explicitly registered (stored), excluding the
    /// internal device base-locations that get auto-added while resolving a
    /// container/EncFS URI. This is what the locations list should show.
    /// </summary>
    public IReadOnlyList<ILocation> GetRegisteredLocations()
        => GetLocations(false).Where(l => _locations.IsStoredLocation(l.GetId())).ToList();

    /// <summary>Finds a loaded location by id (e.g. one passed between pages).</summary>
    public ILocation? FindLocation(string id) => _locations.FindExistingLocation(id);

    public void RemoveLocation(ILocation location) => _locations.RemoveLocation(location);

    /// <summary>Registers a plain device folder as a location.</summary>
    public DeviceLocation AddDeviceLocation(string directoryPath, bool store = true)
    {
        var loc = new DeviceLocation(_settings, Path.GetFullPath(directoryPath));
        _locations.AddNewLocation(loc, store);
        return loc;
    }

    /// <summary>Registers a TrueCrypt/VeraCrypt/LUKS container file as a location.</summary>
    public ContainerLocation AddContainerLocation(string containerFilePath, bool store = true)
    {
        string full = Path.GetFullPath(containerFilePath);
        string dir = Path.GetDirectoryName(full) ?? throw new ArgumentException("Invalid container path", nameof(containerFilePath));
        string name = Path.GetFileName(full);
        var baseLoc = new DeviceLocation(_settings, dir, "/" + name);
        var loc = new ContainerLocation(_settings, baseLoc);
        _locations.AddNewLocation(loc, store);
        return loc;
    }

    /// <summary>
    /// Creates a new container file (header + optional FAT), off the caller's thread
    /// since key derivation is slow, then optionally registers it as a location.
    /// Consumes <paramref name="password"/>. Returns the registered location, or
    /// null when <paramref name="registerAsLocation"/> is false.
    /// </summary>
    public Task<ContainerLocation?> CreateContainerAsync(
        string containerFilePath,
        SecureBuffer password,
        ContainerCreator.Options options,
        bool registerAsLocation = true,
        bool store = true,
        CancellationToken ct = default)
    {
        return Task.Run<ContainerLocation?>(() =>
        {
            var pw = password.GetBytes();
            try { ContainerCreator.Create(containerFilePath, pw, options); }
            finally { Array.Clear(pw); password.Dispose(); }
            return registerAsLocation ? AddContainerLocation(containerFilePath, store) : null;
        }, ct);
    }

    /// <summary>Registers an EncFS directory as a location.</summary>
    public EncFsLocation AddEncFsLocation(string directoryPath, bool store = true)
    {
        var baseLoc = new DeviceLocation(_settings, Path.GetFullPath(directoryPath));
        var loc = new EncFsLocation(_settings, baseLoc);
        _locations.AddNewLocation(loc, store);
        return loc;
    }

    // ---- open / close --------------------------------------------------

    /// <summary>
    /// Opens an encrypted location with the given secrets, off the caller's thread
    /// (the KDF sweep can take seconds). Faults with the relevant exception
    /// (e.g. <c>WrongPasswordException</c>) if it can't open. The location takes
    /// ownership of <paramref name="password"/>.
    /// </summary>
    public Task OpenAsync(
        IEdsLocation location,
        SecureBuffer password,
        IReadOnlyList<Func<Stream>>? keyfiles = null,
        int pim = 0,
        bool readOnly = false,
        IContainerOpeningProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        location.SetPassword(password);
        location.SetKeyfiles(keyfiles);
        if (pim > 0) location.SetNumKdfIterations(pim);
        location.SetOpenReadOnly2(readOnly); // the flag ContainerLocation.Open reads (in-memory, not persisted)
        location.SetOpeningProgressReporter(reporter);

        return Task.Run(() =>
        {
            location.Open();
            if (_settings.MaxContainerInactivityTime > 0 && location.GetAutoCloseTimeout() <= 0)
                location.SetAutoCloseTimeout(_settings.MaxContainerInactivityTime);
            _locations.RegOpenedLocation(location);
        }, ct);
    }

    public void Close(ILocation location, bool force = false) => _locations.CloseLocation(location, force);

    public void CloseAll(bool force = false) => _locations.CloseAllLocations(force);

    /// <summary>
    /// Re-keys an already-open container location with a new password (and optional
    /// new keyfiles / PIM), off the caller's thread. The container must have been
    /// opened read-write. Consumes <paramref name="newPassword"/>.
    /// </summary>
    public Task ChangeContainerPasswordAsync(
        ContainerLocation location,
        SecureBuffer newPassword,
        IReadOnlyList<Func<Stream>>? newKeyfiles = null,
        int newPim = 0,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var pw = newPassword.GetBytes();
            try { location.ChangePassword(pw, newKeyfiles, newPim); }
            finally { Array.Clear(pw); newPassword.Dispose(); }
        }, ct);
    }

    public bool HasOpenLocations() => _locations.HasOpenLocations();

    public IFileSystem GetFileSystem(ILocation location) => location.GetFileSystem();

    // ---- browsing ------------------------------------------------------

    /// <summary>Materialises a directory listing (the underlying iterator is disposed).</summary>
    public IReadOnlyList<IPath> List(IPath directory)
    {
        using var contents = directory.GetDirectory().List();
        return contents.ToList();
    }

    /// <summary>Lists a directory as sorted, filtered <see cref="FileListItem"/>s for the file manager.</summary>
    public IReadOnlyList<FileListItem> Browse(IPath directory, FileListOptions? options = null)
        => _lister.List(directory.GetDirectory(), options);

    public IReadOnlyList<FileListItem> Browse(IDirectory directory, FileListOptions? options = null)
        => _lister.List(directory, options);

    // ---- auto-close ----------------------------------------------------

    public int CloseIdleLocations() => _autoClose.CloseIdleLocations();

    /// <summary>Starts the background idle-lock loop; cancel the token to stop it.</summary>
    public Task StartAutoCloseAsync(TimeSpan interval, CancellationToken ct)
        => _autoClose.RunAsync(interval, ct);

    // ---- file operations (delegated to the queue) ----------------------

    public Task<FileOperationResult> CopyAsync(IReadOnlyList<IPath> sources, IDirectory destination,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default)
        => _fileOps.CopyAsync(sources, destination, progress, ct);

    public Task<FileOperationResult> MoveAsync(IReadOnlyList<IPath> sources, IDirectory destination,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default)
        => _fileOps.MoveAsync(sources, destination, progress, ct);

    public Task<FileOperationResult> DeleteAsync(IReadOnlyList<IPath> sources,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default)
        => _fileOps.DeleteAsync(sources, progress, ct);

    public Task<FileOperationResult> WipeAsync(IReadOnlyList<IPath> sources, int passes = 1,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default)
        => _fileOps.WipeAsync(sources, passes, progress, ct);

    // ---- external open (decrypt → temp → open → re-encrypt) ------------

    public bool CanOpenExternally => _externalOpener != null;

    /// <summary>
    /// Decrypts a file to a temp copy, hands it to the platform opener, and writes
    /// back any changes when the interaction finishes. Returns whether changes were
    /// saved. Requires an <see cref="IExternalFileOpener"/> to have been supplied.
    /// </summary>
    public Task<bool> OpenFileExternallyAsync(IFile file, string? mimeType = null, CancellationToken ct = default)
    {
        if (_externalOpener == null)
            throw new InvalidOperationException("No external file opener is configured on this platform.");
        return _tempFiles.OpenAndTrackAsync(file, _externalOpener, mimeType, ct);
    }

    /// <summary>Decrypts a file to a temp copy and records a baseline (for edit-and-save-back).</summary>
    public TempFileHandle PrepareTempFile(IFile file) => _tempFiles.PrepareTempFile(file);

    /// <summary>Launches an already-prepared temp path in an external app.</summary>
    public Task LaunchExternalAsync(string tempFilePath, string? mimeType = null, CancellationToken ct = default)
    {
        if (_externalOpener == null)
            throw new InvalidOperationException("No external file opener is configured on this platform.");
        return _externalOpener.OpenAsync(tempFilePath, mimeType, ct);
    }

    /// <summary>Re-encrypts a temp file back into its source if it changed. Returns whether it did.</summary>
    public bool SaveTempChanges(TempFileHandle handle) => _tempFiles.SaveChanges(handle);

    /// <summary>Wipes a single temp file.</summary>
    public void ClearTempFile(TempFileHandle handle) => _tempFiles.Clear(handle);

    /// <summary>Wipes any decrypted temp files (call on lock/exit).</summary>
    public void ClearTempFiles() => _tempFiles.ClearAll();

    // ---- clipboard (copy/cut/paste) -----------------------------------

    public bool ClipboardHasItems => _clipboard.Count > 0;
    public bool ClipboardIsCut => _clipboardCut;
    public int ClipboardCount => _clipboard.Count;

    /// <summary>Puts paths on the clipboard for a later paste (cut = move on paste).</summary>
    public void SetClipboard(IEnumerable<IPath> items, bool cut)
    {
        _clipboard.Clear();
        _clipboard.AddRange(items);
        _clipboardCut = cut;
    }

    public void ClearClipboard()
    {
        _clipboard.Clear();
        _clipboardCut = false;
    }

    /// <summary>
    /// Pastes the clipboard into <paramref name="destination"/> (move if the items
    /// were cut, otherwise copy). Clears the clipboard on a cut. Works across
    /// locations/filesystems.
    /// </summary>
    public async Task<FileOperationResult> PasteAsync(IDirectory destination,
        IProgress<FileOperationStatus>? progress = null, CancellationToken ct = default)
    {
        if (_clipboard.Count == 0) return FileOperationResult.Ok;
        var items = _clipboard.ToList();
        var result = _clipboardCut
            ? await MoveAsync(items, destination, progress, ct).ConfigureAwait(false)
            : await CopyAsync(items, destination, progress, ct).ConfigureAwait(false);
        if (_clipboardCut && result.Success) ClearClipboard();
        return result;
    }
}
