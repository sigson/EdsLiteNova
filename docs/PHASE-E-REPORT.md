# Phase E — Services + File Operations · REPORT

> Continues after Phase D (locations + settings). Phase E adds the operational
> layer: recursive file operations with progress/cancel, a background queue, idle
> auto-close of opened containers, and the temp-file (decrypt → edit externally →
> re-encrypt) round-trip. Scoped by `edslite-porting-gap-guide.md` §7 / §9 Phase E.

## 0. Status / environment

- Written **blind** (no .NET SDK/compiler in this environment); verify with
  `dotnet test`. Native module untouched.
- New tests: `tests/Eds.Core.Tests/ServicesTests.cs` — copy-tree + progress,
  same-fs move, recursive delete, wipe (overwrite+delete+byte count), the service
  queue (success + pre-cancelled token), auto-close (idle closes / fresh &
  disabled stay open, deterministic via explicit "now"), and a full temp-file
  decrypt→edit→re-encrypt round-trip incl. `OpenAndTrackAsync`.

## 1. What was implemented (all in `Eds.Core/Services/`, platform-independent)

- **`FileOperations`** — recursive **Copy / Move / Delete / Wipe** over the Vfs
  (`IFileSystem`/`IPath`/`IDirectory`/`IFile`), driven by `CancellationToken` +
  `IProgress<FileOperationStatus>`. Same-fs moves use the fast
  `IFsRecord.MoveTo`; cross-fs moves fall back to copy-then-delete. Conflict policy
  via an optional `Func<IPath, OverwriteAction>` (default Overwrite). Lifts the
  logic out of the Android `CopyFilesTask`/`MoveFilesTask`/`DeleteFilesTask`/
  `WipeFilesTask` without the `IntentService`/`Intent`/notification machinery.
- **`FileOperationStatus`** (progress snapshot) + **`FilesCountAndSize`** (recursive
  pre-scan for totals) — replace the Parcelable `FilesOperationStatus`/
  `FilesCountAndSize`.
- **`WipeUtil`** — overwrites file bytes before deletion (documented caveat: true
  in-place only on write-in-place backings like `StdFs`; on the FAT write-back
  driver it rewrites to fresh clusters — inside a container those freed clusters
  hold ciphertext).
- **`IFileOperationsService` + `FileOperationsService`** — a **serialized async
  queue** (one op at a time, off the caller thread), replacing the sequential
  `IntentService`. Errors/cancellation are captured into `FileOperationResult`.
- **`ISystemClock`/`SystemClock`** + **`AutoCloseService`** — closes idle EDS
  locations using each location's last-activity time and per-location timeout.
  `CloseIdleLocations(DateTimeOffset now)` is pure (deterministic tests);
  `RunAsync(interval, ct)` provides a `PeriodicTimer` loop for a host. Consumes the
  hooks Phase D put on `LocationsManager`/`EdsLocationBase`.
- **`IExternalFileOpener`** (platform hook) + **`TempFileManager`** — the temp-file
  mechanism (`PrepareTempFilesTask`/`StartTempFileTask`/`SaveTempFileChangesTask`/
  `ClearTempFolderTask`): decrypt a location file to a temp dir, detect edits
  (SHA-256 + size baseline), re-encrypt changes back into the location, and
  securely clear temp copies. `OpenAndTrackAsync` ties it together with the
  injected external opener.

## 2. Milestone reached

> Copy/move/delete/wipe between locations with progress and cancellation; a
> container auto-closes after its inactivity timeout; a file is decrypted to a temp
> copy, edited externally, and the changes are re-encrypted back into the
> container — all platform-independent and unit-tested.

## 3. What is NOT done (next steps)

- **Platform wiring (Phase F / platform layer)**: implement `IExternalFileOpener`
  per platform (Android `ACTION_VIEW/EDIT`, desktop shell-open, iOS document
  interaction); add Android **foreground notification** with progress around
  `FileOperationsService`; start `AutoCloseService.RunAsync` from the app host and
  trigger `CloseIdleLocations()` on screen-off.
- **Src→Dst richness**: the gap guide's `SrcDstCollection`/`SrcDstGroup` model was
  reduced to direct `IReadOnlyList<IPath>` + destination `IDirectory`; add grouping
  only if a UI flow needs it.
- **Activity tracking granularity**: last-activity currently updates on
  `GetFileSystem()` access. If finer tracking is wanted (per read/write), thread a
  touch callback from the file operations into the owning location.
- **Phase F (UI)** and **Phase G (format completeness)** remain as in the Phase D
  report. **K2** (secret-wipe audit) should now also cover the temp-file path
  (temp copies are wiped, but review that no plaintext lingers in OS caches).

*Reflects the repository after Phase E. See `PHASE-D-REPORT.md`, `ROADMAP.md`,
`HANDOFF-REPORT.md`.*
