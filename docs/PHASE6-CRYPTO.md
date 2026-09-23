# PHASE6-CRYPTO.md — managed cryptographic primitives

All primitives are written in managed C# under `src/ddk/Crypto/`, run in
the Tier‑0 JIT world (shared DDK assembly), and are validated by
`cryptotest` **30/30 KAT vectors** on device (`docs`-referenced run:
`build/p6-crypto-test.sh`, output `[cryptotest] PASS 30/30 vectors`).

## Inventory

| File | Primitive | Notes |
|------|-----------|-------|
| `Sha1.cs` | SHA‑1 | legacy (SSH mac-sha1 not enabled by default) |
| `Sha256.cs` | SHA‑256 | TLS 1.3 AES‑128‑GCM suite, SSH KDF/MAC |
| `Sha384.cs`/`Sha512.cs` | SHA‑384/512 | TLS 1.3 AES‑256‑GCM suite, hmac-sha2-512 |
| `Md5.cs` | MD5 | legacy only |
| `Hmac.cs` | HMAC (RFC 2104) | all hash kinds |
| `Hkdf.cs` | HKDF (RFC 5869) | TLS 1.3 key schedule |
| `Aes.cs` | AES block cipher | 128/256, generated S‑box |
| `AesCtrStream.cs` | AES‑CTR stream | continuous keystream across SSH packets |
| `AesGcm.cs` | AES‑GCM (SP 800‑38D) | SSH aes-gcm, TLS 1.3 records |
| `ChaCha20.cs` / `Poly1305.cs` | ChaCha20 + Poly1305 | RFC 8439 |
| `ChaCha20Poly1305.cs` | ChaCha20‑Poly1305 AEAD | KAT-verified |
| `X25519.cs` | X25519 (RFC 7748) | SSH curve25519-sha256, TLS 1.3 key share |
| `Ed25519.cs` | Ed25519 (RFC 8032) | SSH host keys, TLS certificates |
| `Scrypt.cs` | scrypt (RFC 7914) | `/etc/shadow` password hashes |
| `Csprng.cs` | CSPRNG | seeded from `Kernel_GetEntropy` |

## Verification

- KATs cover: SHA‑1/256/384/512, MD5, HMAC‑SHA1/256/512,
  X25519 (incl. 1000-iteration), Ed25519 (3 RFC 8032 vectors + verify),
  AES‑128/256 ECB, AES‑128‑CTR, AES‑128‑GCM (empty/ct/tag),
  Poly1305, ChaCha20‑Poly1305, HKDF, scrypt (3 RFC 7914 vectors),
  CSPRNG repeatability.
- TLS 1.3 key schedule cross-checked against RFC 8448 published values
  (e.g. `derived` = `6f2615a1…`, confirming the empty-transcript
  context convention).
- Interop proof: OpenSSH 9.6 client completes full handshakes and
  Windows OpenSSH runs sessions against the in-guest server.

## Bugs found during KAT work (kept for regression awareness)

- AES S‑box generation must use generator **3** (not 2 — order 51).
- Poly1305: 5·r term pairs limb-wise R4()…R1() and packs limbs with
  `& 0xFFFFFFFF`.
- ChaCha20‑Poly1305 AAD/trailer lengths are **bytes** (TCP/TLS GCM
  lengths are bits) — mixed up originally.
- Ed25519 `FeToBytes` must `FeNormalize` loose values (≥ 2p) before
  canonicalization.

## JIT-world constraints that shaped the code

- No `string + char`, `int.ToString()`, `Array.CopyTo`, `String.Trim`,
  `String.IndexOf(char,int)` in hot paths used by services — replaced
  with manual scans / `Util` helpers.
- `byte[] -> ReadOnlySpan<byte>` implicit conversions are unresolvable;
  sockets are called through `fixed (byte* p …)` pointer overloads.
- HMAC/KDF callers keep keys at **exact** algorithm lengths
  (hmac-sha2-256 → 32 bytes, sha512 → 64); deriving extra bytes and
  using them all changes the key (this caused a real SSH interop bug).
