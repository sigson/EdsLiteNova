# Phase D — Locations + Settings (state-of-the-app core) · REPORT

> Continues the port after Phases A/B/C (crypto-fill, FS abstraction + StdFs,
> EncFS). Phase D adds the **state layer**: the app can now register, persist and
> re-open storage locations (device folders, containers, EncFS volumes), which is
> the milestone that unblocks the service layer (E) and the UI (F).
> Complements `HANDOFF-REPORT.md` and `ROADMAP.md`; scoped by
> `edslite-porting-gap-guide.md` §5–§6 and §9 Phase D.

## 0. Status / environment

- No .NET SDK or C# compiler was available in this environment (network blocked;
  only `gcc`, no `cmake`). As in the A/B/C chats, the C# was written **blind**
  against the existing conventions and must be verified with `dotnet test` on
  first run. The native `edscrypto` module was untouched (staged linux-x64 lib is
  already present).
- New tests: `tests/Eds.Core.Tests/LocationsTests.cs` (URI round-trip, SimpleCrypto,
  external-settings persistence, device location, **container register→persist→
  reopen→write/read**, **EncFS register→persist→reopen→read**, manager events,
  wrong-password). Run them first.
- New console demo: `eds locations` runs the Phase D end-to-end scenario
  (create container → register → persist → simulated restart → reopen by password
  → mount → write/read a file → close).

## 1. What was implemented

Everything is **platform-independent** and lives in `Eds.Core` (plus one type in
`Eds.Core.Containers`). No MAUI/Android dependencies; `Uri`→`LocationUri`,
`org.json`→`System.Text.Json`, broadcasts→C# events, `Context`/`SharedPreferences`
→ injected `ISettings`.

### Settings (`Eds.Core/Settings/`)
- `ISettings` — the lean subset the locations layer needs: stored-locations list
  (JSON array of URIs) + per-location settings blob + `NeverSaveHistory` +
  `MaxContainerInactivityTime`. Android-only members dropped.
- `InMemorySettings` — dictionary-backed; `Load`/`Store` are `virtual` so a
  persistent host impl can back it with MAUI `Preferences`/`SecureStorage` or a
  file.

### Crypto (`Eds.Core/Crypto/SimpleCrypto.cs`) — gap guide §2.5
- `CalcStringMd5` (stable location ids), hex helpers, and AES-CBC-256
  `Encrypt`/`Decrypt` (PBKDF2-HMAC-SHA256 key from the protection key; random
  salt+IV; self-describing base64) for protecting the saved password. Compatibility
  with old Android blobs is intentionally *not* preserved (per the gap guide).
- `ContainerProgress.cs` (the opening-progress reporter) was **relocated into the
  Core assembly** while keeping its `Eds.Core.Containers` namespace, so both the
  container reader and `IOpenableLocation` can depend on it without a Core→Containers
  cycle. Zero changes to existing referencing files.

### Locations (`Eds.Core/Locations/`)
- `LocationUri` — a small, predictable URI value (scheme + '/'-path + ordered
  query) that reliably round-trips the **nested base-location URI** an EDS location
  embeds; avoids `System.Uri`'s canonicalisation pitfalls.
- Interfaces `ILocation` / `IOpenableLocation` / `IEdsLocation` (+ `IExternalSettings`,
  `IProtectionKeyProvider`) — de-Androidised ports of `Location`/`Openable`/
  `EDSLocation`.
- `LocationBase` — current-path + external-settings JSON persistence with
  protected fields (self-describing `h:`/`e:` prefixes).
- `OpenableLocationBase` — transient password (`SecureBuffer`), KDF override,
  read-only, opening reporter; saved-password / custom-KDF settings.
- `EdsLocationBase` — layers over a base location, lazily mounts the inner FS once
  opened, id = MD5(base URI), open-read-only / auto-close-timeout settings.
- `DeviceLocation` — folder on device over `StdFs` (the base for container/EncFS
  locations).
- `EncFsLocation` — mounts `EncFsFs` over the base location's directory.
- `LocationsManager` — the central registry: create-from-URI via
  `ILocationFactory`, add/remove/replace, find, load/save stored locations
  (`System.Text.Json`), open-order stack + close-all, `LocationAdded`/`Removed`/
  `Changed` events, id generation, optional protection-key provider.
- `ILocationFactory` + `DeviceLocationFactory` + `EncFsLocationFactory` — the seam
  that keeps Core free of a Containers dependency.

### Containers (`Eds.Core.Containers/Locations/`)
- `ContainerLocation` — **the keystone**: wires the base `ILocation` + `EdsContainer`
  + `EncryptedFileWithCache` + `FatVfs` into one openable location. Port of
  `ContainerBasedLocation` (remembers format/cipher/hash in settings; 64-byte
  password cap).
- `ContainerLocationFactory` — registers the `eds-container` scheme.

## 2. Milestone reached

> Register a container **or** an EncFS volume as a location → persist it → with a
> fresh `LocationsManager` over the same settings (simulated restart) → reload →
> open by password → get a mounted `IFileSystem` → write/read/close. All through
> the locations layer.

Proven by `LocationsTests` (TrueCrypt, LUKS, EncFS) and the `eds locations` demo.
This is the Phase D exit criterion from the gap guide (§9 Phase D) and unblocks E/F.

## 3. Architecture notes / decisions

- **No Core→Containers cycle.** `LocationsManager` dispatches by scheme to
  factories registered at start-up; the host wires:
  `new LocationsManager(settings).RegisterCoreFactories().RegisterFactory(new ContainerLocationFactory())`.
- **Inner FS is `FatVfs`** for containers today. When exFAT (Phase I) or another
  inner FS lands, only `ContainerLocation.CreateInnerFs` changes (pick the FS by
  probing), or a small FS-info sweep is added.
- **Protection key** is optional and injected (`LocationsManager.ProtectionKeyProvider`
  / `LocationBase.ProtectionKeyProvider`); null ⇒ saved passwords stored as hex.
  The platform host should supply a key from Keystore/Keychain/DPAPI (ties into K2).

## 4. What is NOT done (next steps)

- **Phase E — services / file operations**: copy/move/delete/wipe, temp-file
  open-in-external, and the **auto-close timer** (the hooks — open registry, closing
  order, `GetLastActivityTime`, `GetAutoCloseTimeout` — are in place; the timer/
  service that consumes them is Phase E).
- **Phase F — UI**: wire the MAUI shell to `LocationsManager` (list/add/open/close),
  password/keyfile/PIM dialogs, persist. The MAUI `BrowserViewModel` still uses the
  minimal `Eds.Core.Fs.Abstract` adapter and inline container plumbing — Phase F
  should switch it to `LocationsManager` + `FatVfs`.
- **Phase G (parallel) — format completeness**: `ContainerLocation` persists
  format/cipher/hash hints but `EdsContainer.Open` does not yet consume them
  (still sweeps); keyfiles/PIM/hidden-volume fields are absent from settings.
- **Platform settings impl**: an `ISettings` over MAUI `Preferences`/`SecureStorage`
  (subclass `InMemorySettings` overriding `Load`/`Store`).
- **Cross-tasks**: K1 (real reference volumes) and K2 (secret-wipe audit of the new
  key/password paths) still open — the new saved-password path and EncFS/container
  master keys should be swept.

*Reflects the repository after Phase D. Numbers/details from source. See
`ROADMAP.md` for the living map and `HANDOFF-REPORT.md` for the A/B/C hand-off.*
