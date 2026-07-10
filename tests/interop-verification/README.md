# Interop verification harness (cross-task K1)

This directory independently proves that EdsLite's **EncFS** and **LUKS1** decode
paths are byte-for-byte compatible with volumes created by the genuine desktop
tools (`encfs`, `cryptsetup`) — the single most important remaining risk called
out as **K1** in `docs/HANDOFF-REPORT.md`.

## Why it exists

Every other crypto/FS test in this repo is a **round-trip**: the port writes and
then reads its own data, which proves internal consistency but *not* interop with
real third-party data. This harness closes that gap for the two formats that can
be generated with tools available on Linux.

## How it works

The Python here is a **line-by-line transcription of the C# decode logic**
(`EncFsVolumeKey`, `CipherBase` / `StreamCipherBase`, `Sha1MacCalculator`,
`BlockNameCipher`, `B64`, `LuksLayout`, `Af`). Crucially, it calls the **same
`libedscrypto`** native library the C# port P/Invokes (via `ctypes`), so AES /
CBC / CFB / XTS are the identical implementations.

It then decrypts the **real reference artifacts** in
`../Eds.Core.Tests/fixtures/interop/` and checks:

- **EncFS**: decrypted filenames (block codec + chained name IV across nested
  dirs) and file contents (per-file IV header, per-block CBC `blockIndex ⊕ fileIV`,
  stream-CFB tail, multi-block files) match the known plaintext.
- **LUKS1**: the recovered master key equals **cryptsetup's own
  `luksDump --dump-master-key`** output, for AES-XTS with SHA-1/256/512 and
  256/512-bit keys.

A pass therefore demonstrates the *algorithm* (as transcribed into C#) is
interop-correct, independently of the .NET SDK.

## Run it

```bash
# 1) build the native lib the port and this harness share
./scripts/build-native.sh                 # produces native/build/libedscrypto.so

# 2) run the verification
python3 tests/interop-verification/verify_interop.py
# → "RESULT: ALL INTEROP CHECKS PASSED"
```

Only Python 3 stdlib is required (`hashlib`, `hmac`, `ctypes`). Set
`EDS_NATIVE_LIB=/path/to/libedscrypto.so` if the library is built elsewhere.

## Relationship to the C# tests

The C# tests `EncFsRealInteropTests` and `LuksRealInteropTests` open the *same*
fixtures through the real `EncFsFs` / `LuksLayout` public API. This harness is the
independent oracle that establishes the *expected* values those tests assert, and
lets the interop claim be re-verified in environments where the .NET SDK is
unavailable (as it was while the core was being ported).

## Regenerating the fixtures

See `../Eds.Core.Tests/fixtures/interop/MANIFEST.md`. Regeneration requires
`encfs` and `cryptsetup` and changes all salts/keys, so the expected values in the
manifest, this harness, and the C# tests must be updated together. Prefer to keep
the committed artifacts stable.
