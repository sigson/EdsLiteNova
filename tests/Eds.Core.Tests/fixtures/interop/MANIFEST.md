# Interop reference artifacts — provenance & expected values

These are **real** encrypted volumes produced by the genuine desktop tools, used to
prove byte-for-byte data compatibility (cross-task **K1**). They are not created by
this port. Do not regenerate casually — regenerating changes salts/keys and the
expected values below.

**Password for every artifact: `testpass123`.**

---

## `encfs-standard/` — EncFS volume (real `encfs` 1.9.5, "standard" mode)

Created with `encfs --standard` (FUSE), then populated with known files, then
unmounted. The tree holds the encrypted files with encrypted names; the config is
stored as `encfs6.xml` (the port's `Config.GetConfigFile` accepts both
`.encfs6.xml` and `encfs6.xml`, and directory listings filter it out).

Config: `cipherAlg=ssl/aes`, `nameAlg=nameio/block`, `keySize=192`, `blockSize=1024`,
`uniqueIV=1`, `chainedNameIV=1`, `externalIVChaining=0`, `blockMACBytes=0`,
`kdfIterations=486144`.

Derived master key (40 bytes, independently reproduced by the verification harness):
`a61d8399e72a71caaf367cf9bfbb00e334023206e9e9d2f150555b1c14ef2cbd937e6ee27f64329c`

Plaintext contents (decrypted name → data):

| decrypted path      | size | content / sha256 |
|---------------------|------|------------------|
| `readme.txt`        | 19   | `hello encfs interop` |
| `lines.txt`         | 4200 | `"L%04d\n"` for i in 0..699 (sha256 `cfdc101c…047438`) |
| `empty.dat`         | 0    | (empty) |
| `sub/note.md`       | 25   | `nested file contents here` |
| `sub/deep/data.bin` | 4096 | random, sha256 `b698ea901044341f5224728628ed6fe92d03c4d23ba216cc18936584be729f06` |

Exercises: PBKDF2-HMAC-SHA1 volume-key unwrap, block filename codec + B64,
chained name IV across nested dirs, per-file IV header, per-block AES-CBC
(`blockIndex ⊕ fileIV`), stream AES-CFB tail, multi-block files.

---

## `luks/*.luks` — LUKS1 headers (real `cryptsetup` 2.7.0)

Created with `cryptsetup luksFormat --type luks1`. Each file is the **header +
keyslot-0 AF key material**, truncated to 256 KiB (before the 2 MiB payload) to keep
the repo small — enough to derive and validate the master key. Only keyslot 0 is
enabled.

Master keys below are cryptsetup's own `luksDump --dump-master-key` output, and are
**independently reproduced bit-for-bit** by the verification harness (see
`../../interop-verification/`).

| file | cipher | hash | key bits | uuid | master key (cryptsetup ground truth) |
|------|--------|------|----------|------|--------------------------------------|
| `aes-xts-sha256-512.luks` | aes-xts-plain64 | sha256 | 512 | `28885b1d-…f492f556` | `05edfc53caa29762baf4a2a1944631e731aef8d31ccfaf0dcc6470b6ab685b30e628cda0c0c8f563e29f39f8b61469b1f918869dff1e8d81ff466394e6d535f1` |
| `aes-xts-sha512-512.luks` | aes-xts-plain64 | sha512 | 512 | `7ab6f82b-…59d97be6` | `1db0b4e73f87387c424be8611557874756888e24da5fe314bfe0c04e737ea458c2a17f878f6c65fcaaa4d5c42e8854150df97f7dc2786076b37d7ec548ae71aa` |
| `aes-xts-sha1-256.luks`   | aes-xts-plain64 | sha1   | 256 | `6d7445e3-…affa5d1d5` | `03529ce7f4a1b9872737cf06f1fe903d68751d132e0b3996c59f36080790a73b` |

Exercises: LUKS1 big-endian header parse, PBKDF2-HMAC-{SHA1,SHA256,SHA512},
AES-XTS-plain64 decrypt of AF material, AF-merge (hash diffuse), master-key digest
verification.

Serpent/Twofish LUKS volumes are absent: the generating sandbox lacked the kernel
dm-crypt modules `cryptsetup` needs to validate those ciphers at format time. Those
block ciphers are covered by published KAT vectors in `CryptoKatTests` /
the native KAT harness.

---

## How these were generated / re-verified

See `tests/interop-verification/README.md`. The harness re-derives every value
above from the artifacts using the *same* `libedscrypto` primitives the C# port
P/Invokes, so it is an independent check that the port's documented algorithm is
interop-correct — reproducible without the .NET SDK.
