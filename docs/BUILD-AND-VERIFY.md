# BUILD & VERIFY — checkpoint after Phases D / E / G (+ F-prep)

> Everything since Phase D was written **without a compiler in the authoring
> environment** (no .NET SDK, no network). It targets the existing conventions and
> was cross-checked statically against the real interfaces, but the first thing to
> do is compile and run the tests. This guide lists exactly what to run, what each
> new test proves, and where to look first if something doesn't build.

## 1. Build & test

```bash
# from the repo root (the folder with the .sln / src / tests)
dotnet build
dotnet test                     # runs the xUnit suite in tests/Eds.Core.Tests
# focus a single area if needed:
dotnet test --filter FullyQualifiedName~LocationsTests
dotnet test --filter FullyQualifiedName~ServicesTests
dotnet test --filter FullyQualifiedName~ContainerFormatTests
dotnet test --filter FullyQualifiedName~SecureInputTests
```

Native `edscrypto` is already staged for `linux-x64`; the crypto tests need it on
the run RID. No native changes were made in D/E/G.

## 2. What the new tests prove

- **`ContainerFormatTests`** (Phase G)
  - Keyfile open round-trip (+ order independence, wrong/absent keyfile fails).
  - Keyfile CRC pinned to the published CRC-32/MPEG-2 check value
    (`"123456789"` → `0x0376E6E7`) — validates the table byte-for-byte.
  - VeraCrypt PIM: wrong PIM fails to open.
  - Change password: TrueCrypt/VeraCrypt (header re-encrypt) **and LUKS** (keyslot
    re-key) — data survives, old password stops working.
  - Format-capability metadata; keyfile-mixer no-op/determinism.
  - *Speed:* uses VeraCrypt with PIM 5 (~20k KDF iters) so opens are fast; LUKS uses
    its small default iterations.
- **`LocationsTests`** (Phase D + D/G integration)
  - `LocationUri` nested round-trip; `SimpleCrypto`; external-settings persistence.
  - Container register → persist → **reopen after a simulated restart** → write/read
    (TrueCrypt, LUKS); wrong password rejected; EncFS the same.
  - Keyfile + PIM **persisted in the location** and used to reopen by password alone.
- **`ServicesTests`** (Phase E)
  - Recursive copy (+progress), same-fs move, recursive delete, wipe (+byte count).
  - `FileOperationsService` success + pre-cancelled token.
  - `AutoCloseService` closes idle / leaves fresh & disabled open (deterministic).
  - `TempFileManager` decrypt → edit → re-encrypt round-trip incl. `OpenAndTrackAsync`.
- **`SecureInputTests`** (Phase F prep)
  - `EditableSecureBuffer` edit/grow/encode/clear/dispose/`CloseAll`/bounds.

## 3. If the build breaks, look here first (highest-risk, because blind-written)

Ordered by where a subtle slip is most likely:

1. **`src/Eds.Core/Locations/EdsLocationBase.cs`** — deep nested-settings-class
   hierarchy (`ExternalSettingsBase` → `OpenableExternalSettings` → `EdsExternalSettings`).
   If a nested type or protected member doesn't resolve, it's here.
2. **`src/Eds.Core/Locations/LocationsManager.cs`** — re-entrant locking + JSON
   persistence; and `CreateLocationFromUri` propagating the protection-key provider.
3. **`src/Eds.Core.Containers/EdsContainer.cs`** — the two `Open` overloads (reporter
   vs `ContainerOpenOptions`) and the `ChangePassword` `switch`. A `null` literal
   passed as the 2nd `Open` arg would be ambiguous (no current caller does this).
4. **`src/Eds.Core.Containers/Locations/ContainerLocation.cs`** — `EffectiveKeyfiles`
   uses `global::Eds.Core.Containers.Keyfiles` because the inherited `Keyfiles`
   property shadows the type name.
5. **`src/Eds.Core/Crypto/ContainerProgress.cs`** — physically in the `Eds.Core`
   assembly but declared in namespace `Eds.Core.Containers` on purpose (shared
   progress contract without a Core→Containers cycle). If you see a duplicate-type
   or missing-type error around the reporter, it's this arrangement.
6. **`src/Eds.Core/Services/FileOperations.cs`** — the nested `Adapter` reaches into
   the outer instance's private counters (legal, but unusual).

All the cross-cutting API assumptions these files make (the Vfs `IPath`/`IFile`/
`IDirectory`/`IFileProgressInfo` members, `IRandomAccessIO.Length()/Flush()`,
`IFile.CopyToOutputStream(Stream,long,long,IFileProgressInfo?)`) were verified
against `src/Eds.Core/Fs/Vfs/FileSystemModel.cs` and `IRandomAccessIO.cs` and match.

## 4. Likely-benign warnings (won't fail the build)

`TreatWarningsAsErrors=false`, so nullable-reference warnings won't break it. Expect
some around JSON node casts and `SecureBuffer`/`EditableSecureBuffer` finalizers.

## 5. Still outstanding (need an environment/inputs not available while authoring)

- **K1 — reference volumes**: byte-exact parity for containers/EncFS made by real
  TrueCrypt/VeraCrypt/cryptsetup/EncFS. The keyfile CRC is now externally validated;
  the remaining unverified piece is the full keyfile pool and end-to-end volume open.
  Needs real artifacts (network/tools).
- **Phase F (MAUI UI)**: now written — a Locations → Unlock → Browser flow plus
  Create, all bound to `EdsAppController` (see `PHASE-F-REPORT.md`). It needs the
  MAUI workload to build; the core-only `dotnet build` above does not include it.
  To build the UI head:
  `dotnet build src/Eds.Maui/Eds.Maui.csproj -f net10.0-maccatalyst`
  (or `-windows10.0.19041.0` / `-android` / `-ios`). Expect to iterate on
  XAML/binding details a real build surfaces. Still to do: the platform
  `IExternalFileOpener` ("open with…"), foreground notifications + lifecycle
  auto-close, saved-password protection, and a change-password dialog.
- **Native CTR/ECB, exFAT**: need the native toolchain (CMake) to build the shim.
- **Hidden volumes**: disabled in the lite original and no reference layout to port;
  intentionally not invented.

## 6. Reports

Per-phase detail: `PHASE-D-REPORT.md`, `PHASE-E-REPORT.md`, `PHASE-G-REPORT.md`.
Living map: `ROADMAP.md`. Hand-off overview: `HANDOFF-REPORT.md`.
