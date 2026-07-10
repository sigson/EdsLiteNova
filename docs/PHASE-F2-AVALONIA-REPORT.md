# Phase F — Finish + Avalonia MAUI backend (Linux) · REPORT

> Continues `PHASE-F-REPORT.md`. Closes the four "Not done / remaining" items and
> integrates the **Avalonia MAUI backend** so the app runs on **Linux** (and drawn
> Windows/macOS) from the *same* MAUI codebase.
>
> **Build caveat (unchanged):** the whole UI + platform layer can't be compiled in
> the authoring environment (no .NET SDK / NuGet blocked). This is written against
> the verified `EdsAppController` core and the official Avalonia-MAUI setup docs;
> expect to iterate on a real build (esp. the preview Avalonia backend).

## 1. Protection key — real secret store  ✅

`SecureStoreProtectionKeyProvider` replaces the insecure placeholder. It keeps the
32-byte wrap-key in the OS secret store via MAUI **`SecureStorage`** (Android
Keystore / iOS+macOS Keychain / Windows DPAPI). It caches after first use, hands out
copies (caller can erase freely), and — if the store is unavailable — **degrades** to
the old Preferences provider while exposing `IsSecure=false` so the UI can warn.
Registered in `MauiProgram` in place of `SimpleStoreProtectionKeyProvider` (kept as
the fallback). On the Avalonia/Linux head, `SecureStorage` comes from
`Avalonia.Controls.Maui.Essentials`; if a given preview doesn't implement it yet, the
fallback keeps the saved-secret feature working (see §5 caveats).

## 2. Foreground service for long operations  ✅

- `IForegroundOperationService` / `IForegroundOperationScope` (shared abstraction).
- **Android**: `EdsForegroundService` — a real started/foreground `Service`
  (`foregroundServiceType=dataSync`) that holds an ongoing notification while any
  operation scope is active, so the OS won't kill the process mid-copy.
  `AndroidForegroundOperationService` ref-counts concurrent scopes and starts/stops
  the service via `StartForegroundService`. Manifest updated with
  `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_DATA_SYNC` permissions and the `<service>`
  declaration.
- **Elsewhere** (desktop/iOS/Avalonia): `NoopForegroundOperationService`.
- `VaultBrowserViewModel` wraps **paste** and **import** in
  `await using var scope = _foreground.Begin(...)` alongside the existing notifier.

## 3. Polish — image viewer with EXIF  ✅ (shared core)

`Eds.Core/App/ImageMetadataReader.cs` extracts EXIF/IPTC/GPS/dimensions from a
**decrypted** stream (`IFile.GetInputStream()`), so metadata never hits disk in the
clear. Backed by the **`MetadataExtractor`** NuGet (the .NET port of the same library
the original Android app used). UI-agnostic → usable by both the MAUI and Avalonia
heads. `IsImage(IPath)`, `Read(...)`, `Format(...)`, `TryGetGps(...)`.
*(Multi-select and richer progress UI remain UI-layer polish — best done against a
real build; the service/metadata plumbing they need is in place.)*

## 4. Avalonia MAUI backend — Linux support  ✅ (integrated per official docs)

Per <https://docs.avaloniaui.net/docs/migration/maui/> **Option 1** and the repo
`AvaloniaUI/Avalonia.Controls.Maui` `docs/config-and-setup.md`, the backend renders
the **existing** MAUI XAML/VMs/services through Avalonia — **no separate project, no
rewrite**. Integrated into `Eds.Maui` itself:

- **csproj**: opt-in `net11.0` desktop TFM (`-p:EnableAvalonia=true`), packages
  `Avalonia.Controls.Maui` (+ `.Desktop` on `net11.0`, `.Compatibility`,
  `.Essentials`), version via `$(AvaloniaMauiVersion)`, and an `AVALONIA_DESKTOP`
  compile constant on the desktop TFM.
- **MauiProgram**: `.UseAvaloniaApp()` under `#if AVALONIA_DESKTOP`. The Avalonia
  `Application`/bootstrap are **source-generated** by `Avalonia.Controls.Maui.Desktop`
  — no manual `Program.cs`/`AppBuilder`.
- Default `net10.0` mobile/desktop build is **unchanged** (backend is opt-in).

Build / run on Linux:

```bash
# needs the .NET 11 (preview) SDK + MAUI 11 workload restored
dotnet run --project src/Eds.Maui/Eds.Maui.csproj -f net11.0 -p:EnableAvalonia=true
```

Why this shape: MAUI has no Linux TFM; the plain `net11.0` (no platform suffix) TFM
is the Avalonia-hosted desktop head. On it, the `#if ANDROID` DI branch falls to the
no-op notifier/foreground services automatically, and `Platforms/Android/**`
(SAF provider, notifier, foreground service) is not compiled — MAUI SingleProject
scopes `Platforms/<p>/**` per TFM.

## 5. Caveats to verify on a real build

- **Avalonia MAUI is preview** (GA targeted with .NET MAUI 11). Requires the .NET 11
  preview SDK + MAUI 11 workload. Pin `$(AvaloniaMauiVersion)` to the preview you
  restore (default `11.3.0-preview1` is a placeholder — check NuGet).
- **Essentials coverage is partial in preview** — `SecureStorage`/`FilePicker`/
  `Launcher`/`FolderPicker` may be incomplete on Linux. The key provider already
  degrades gracefully; file/folder pickers in the VMs may need a desktop fallback if
  a given preview lacks them (`STATUS-ESSENTIALS.md` upstream tracks coverage).
- **`CommunityToolkit.Maui`** (FolderPicker) may not yet run under the Avalonia
  backend; if it fails on `net11.0`, swap the two `CommunityToolkit.Storage` calls
  for `Avalonia.Controls.Maui.Essentials` `FolderPicker`/`FilePicker`.
- Expect the general MAUI-build fixes noted originally (Android binding
  signatures / XAML tweaks) plus preview-specific Avalonia handler gaps.

*Binds to the verified core (`PHASE-D/E/G/K1-REPORT.md`). Only the UI + platform
glue is unbuilt here.*
