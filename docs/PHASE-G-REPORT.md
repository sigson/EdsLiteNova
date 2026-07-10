# Phase G — Container Format Completeness · REPORT

> Parallel track (depends only on the finished layouts). Adds keyfiles, VeraCrypt
> PIM plumbing, container password change, and per-format capability metadata.
> Scoped by `edslite-porting-gap-guide.md` §3 (3.1/3.3/3.4/3.6) and §8.6.

## 0. Status / environment

- Written **blind** (no compiler); verify with `dotnet test`. Native module
  untouched.
- New tests: `tests/Eds.Core.Tests/ContainerFormatTests.cs` — keyfile open
  round-trip (+ order independence + negative cases), PIM wrong-value rejection,
  change-password round-trip (data survives; old password stops working;
  adding a keyfile), format-info capabilities, and a keyfile-mixer unit
  (no-keyfiles no-op, determinism, pool-size extension). Uses VeraCrypt with a
  small PIM (~20k iterations) so every open is fast.

## 1. What was implemented

### Keyfiles (§3.3) — `Eds.Core.Containers/Keyfiles.cs`
- `KeyfileMixer` — the TrueCrypt/VeraCrypt keyfile **pool** algorithm: a 64-byte
  pool built from the running CRC of every keyfile byte, then added byte-wise into
  the first 64 bytes of the password (extended to 64 if shorter). No keyfiles ⇒
  password unchanged.
- **Important correctness detail:** the keyfile CRC is TrueCrypt's *non-reflected*
  CRC-32 (poly 0x04C11DB7, MSB-first, no final xor) — deliberately **not** the
  reflected zip CRC-32 in `System.IO.Hashing.Crc32`. The table is generated
  explicitly; using the wrong variant would make real keyfile volumes unopenable.
- `Keyfiles.FromFile`/`FromBytes` build keyfile stream factories.

### PIM (§3.4)
- The layout already computed `15000 + PIM*1000` iterations; Phase G **plumbs it
  through**: `ContainerOpenOptions.Pim`, `ContainerCreator.Options.Pim`, and
  `EdsContainer.Open(password, options)` set it on the VeraCrypt layout.

### Change password (§8.6)
- `EdsContainer.ChangePassword(newPassword, options?)` — keeps the **same master
  key** so data stays readable. **TrueCrypt/VeraCrypt**: re-encrypts the volume
  header (main + backup) with a key derived from the new password/keyfiles/PIM.
  **LUKS**: re-keys the keyslot that opened the volume (fresh slot salt, AF-split of
  the master key under the new password, header rewritten with a fresh master-key
  digest salt); the old password stops opening that slot.

### Open orchestration
- `EdsContainer` gains `Open(byte[], ContainerOpenOptions)` (keyfiles + PIM +
  reporter) alongside the original reporter overload (unchanged, back-compatible).

### Creation
- `ContainerCreator.Options` gains `Keyfiles` and `Pim`; `Create` mixes keyfiles
  (TrueCrypt/VeraCrypt) and applies PIM.

### Format metadata (§3.1) — `ContainerFormatInfo.cs`
- `IContainerFormatInfo` + `ContainerFormats` (LUKS / VeraCrypt / TrueCrypt) expose
  capability flags (hidden/keyfiles/PIM), max password length and open priority, so
  the UI can decide which fields to show without hard-coding format knowledge.
- **Hidden support is reported as `false`** for every format: the port does not
  implement hidden volumes, and the lite original disables them too (its
  `hasHiddenContainerSupport()` returns false and `getHiddenVolumeLayout()` returns
  null — there is no hidden layout in the lite source to port). Keyfiles/PIM are the
  intentional completeness additions here (the gap guide §3.3 asks for keyfiles even
  though lite disables them).

### Locations wiring
- `IOpenableLocation.SetKeyfiles(...)` added; `ContainerLocation.Open` passes PIM
  (`GetSelectedKdfIterations`) and keyfiles through to `EdsContainer.Open`.
- **Persistence:** `ContainerLocation.ContainerExternalSettings` now stores keyfile
  *paths* (not content) and, via the existing `CustomKdfIterations`, the PIM — so a
  keyfile/PIM-protected container registered as a location reopens by password alone
  after a restart (covered by `LocationsTests`). Existing container locations (no
  keyfiles, PIM 0) behave exactly as before.

## 2. What is NOT done

- **Hidden volumes (§3.2)** — not implemented, and reported as unsupported. The
  base header machinery has the hooks (`getHeaderOffset`/`CalcHiddenVolumeSize`), but
  the lite original disables hidden volumes and ships no hidden layout, so there is
  no faithful reference to port; implementing it would mean inventing the layout and
  can't be verified without real hidden volumes (K1). Left for a dedicated pass.
- **LUKS add-keyslot** — `ChangePassword` re-keys the *opening* keyslot; adding a
  second independent keyslot (multi-password) is not yet exposed.
- **Byte-exact compatibility (K1)** — the keyfile/PIM implementations are faithful
  to the documented algorithms and pass internal round-trips. The keyfile **CRC**
  is now pinned to its published check value (CRC-32/MPEG-2 of "123456789" ==
  0x0376E6E7, via `KeyfileMixer.Crc32Mpeg2`), so the trickiest component is
  externally validated. The remaining unverified piece is the exact pool
  combination against a volume produced by real VeraCrypt — confirm with reference
  artifacts (K1).
- **UI (Phase F)** — dialogs to enter keyfiles/PIM and invoke change-password;
  `ContainerLocation.ContainerExternalSettings` can persist PIM / keyfile paths when
  the UI needs them.

*Reflects the repository after Phase G. See `PHASE-D-REPORT.md`, `PHASE-E-REPORT.md`,
`ROADMAP.md`, `HANDOFF-REPORT.md`.*
