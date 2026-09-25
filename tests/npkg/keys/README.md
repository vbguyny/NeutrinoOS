# tests/npkg/keys — TEST-ONLY signing keypair

These keys are **test fixtures only**.  The private key was generated from a
deliberately public, fixed seed and must never be used to sign anything that
matters.

- `private.key` — 64 hex characters (32-byte Ed25519 seed) plus trailing newline.
- `public.key` — 64 hex characters (32-byte Ed25519 public key) plus trailing newline.
- Fingerprint — SHA-256 of the raw 32-byte public key (lowercase hex).

Regenerate with the host CLI:

    npkg-host keygen --seed 0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20 \
        --out-dir tests/npkg/keys --force
