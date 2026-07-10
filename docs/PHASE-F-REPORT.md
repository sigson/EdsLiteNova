# Phase F — MAUI UI (on the application facade) · REPORT

> The user-facing UI, built as a thin binding layer over `EdsAppController`
> (Eds.Core.App). **This is the one layer that could not be compiled or tested in
> the authoring environment** — it needs the .NET MAUI workload. The core it binds
> to (Phases D/E/G + the controller) is compiler- and test-verified.

## 0. Status

- Core (`Eds.Core`, `Eds.Core.Containers`, tests) is green. The MAUI head
  (`Eds.Maui`) was written against the existing conventions and the (verified)
  `EdsAppController` API, but must be built with the MAUI workload:
  `dotnet build src/Eds.Maui/Eds.Maui.csproj -f net10.0-maccatalyst`
  (or the Windows / Android / iOS head). Expect to iterate on XAML/binding details
  that only a real build surfaces.

## 1. What the UI does (all via `EdsAppController`)

- **Locations tab** (`LocationsPage`/`LocationsViewModel`) — the home screen: lists
  the registered locations (persisted via `JsonFileSettings`), with **Add container**
  (file picker) and **Add EncFS folder** (folder picker), and per-row open / close /
  remove. Tapping an encrypted, locked location routes to the unlock page; an
  already-open one (or a plain folder) routes straight to the browser.
- **Unlock page** (`OpenPage`/`OpenViewModel`) — password + optional **PIM** +
  read-only toggle; calls `OpenAsync` (KDF off the UI thread) with live KDF/cipher
  progress; on success routes to the browser.
- **Browser page** (`VaultBrowserPage`/`VaultBrowserViewModel`) — lists the open
  location's filesystem via `Browse` (sorted), navigates in/up, and — when mounted
  read-write — supports **new folder**, **import file** (copies an external file in),
  and **delete** (through the file-operations queue). Close returns to Locations.
- **Create tab** (`CreatePage`/`CreateViewModel`) — format/cipher/hash/size/**PIM**
  + destination; `CreateContainerAsync` builds the volume and registers it, so it
  appears on the Locations tab.
- **Diagnostics tab** (`MainPage`/`MainViewModel`) — unchanged crypto self-test.

DI (`MauiProgram`): a `JsonFileSettings` in `FileSystem.AppDataDirectory` and a
singleton `EdsAppController`; pages/VMs registered; navigation via Shell routes
(`open`, `vault`) with the location id passed as a query parameter.

## 1a. Completed in the follow-up pass

- **External open/edit** — `MauiExternalFileOpener` (`Launcher`) + controller
  `PrepareTempFile`/`LaunchExternalAsync`/`SaveTempChanges`; the browser decrypts a
  file to a temp copy, opens it externally, and writes changes back on “Save changes”.
- **Copy / cut / paste** — a controller-level clipboard; per-row copy/cut and a
  toolbar Paste, working across folders and locations.
- **Sort controls** — field (name/size/date/type) + descending + directories-first
  in the browser, driving `FileLister`.
- **Change-password UI** — `ChangePasswordPage`/VM wired to
  `ChangeContainerPasswordAsync`, reached from the browser (containers only).
- **Settings tab** — auto-lock timeout + never-save-history (persisted via
  `JsonFileSettings`), plus Lock-all / clear-temp actions.
- **Auto-close lifecycle** — the idle loop starts at window creation; opened
  locations inherit the default timeout from settings; everything locks and temp
  files are wiped on window destroy.

## 1b. Android platform layer (this pass)

- **MAUI scaffolding** that was missing entirely: `Platforms/Android` (MainApplication,
  MainActivity, manifest), minimal `Platforms/iOS` + `Platforms/MacCatalyst` entry
  points, `Resources/AppIcon` + `Resources/Splash` SVGs, and csproj icon/splash items
  (the missing OpenSans font reference was removed). Without these no head could build.
- **SAF DocumentsProvider** (`Platforms/Android/EdsDocumentsProvider.cs`, declared in
  the manifest) — exposes every **open** location's decrypted VFS to other apps, so any
  SAF-aware file browser can **list / read / write / create / delete** inside a mounted
  container or EncFS. Files decrypt to a temp copy on open and re-encrypt on close
  (`ParcelFileDescriptor` close listener). This is the intended "access is given via
  SAF" model. Document ids are `"{locationId}|{vfsPath}"`; only open locations appear.
- **Notifications** — `IOperationNotifier` (`AndroidOperationNotifier` on Android, no-op
  elsewhere); the browser shows a progress notification during paste/import.
- **Protection key** — `SimpleStoreProtectionKeyProvider`, wired onto the controller.
  ⚠️ **Deliberately insecure**: it keeps a random key in **plaintext Preferences**, which
  offers no protection. It only exercises the saved-secret path and **must be replaced**
  with a Keystore/Keychain/DPAPI-backed provider.
- A static `AppServices` bridge lets the platform-instantiated provider reach the DI
  singleton controller.

## 2. Not done / remaining

- Replace the placeholder protection-key provider with a real secret-store one.
- Full foreground **service** for long background operations (the notifier posts a
  notification but isn't a bound foreground service).
- Polish: multi-select, image viewer with EXIF, richer operation-progress UI.
- **Verify/iterate on a real MAUI build** — the whole UI + Android layer is unbuilt
  here; expect Android-binding name/signature fixes (cursor column enums,
  `ParcelFileDescriptor` overloads) and XAML tweaks.

*Binds to the verified core; see `PHASE-D/E/G-REPORT.md`, `ROADMAP.md`,
`HANDOFF-REPORT.md`, `BUILD-AND-VERIFY.md`.*
