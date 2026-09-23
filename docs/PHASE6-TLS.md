# PHASE6-TLS.md — TLS 1.3 and X.509 for HTTPS

## Scope

`src/ddk/Tls/` implements a **TLS 1.3 server** (RFC 8446) sufficient for
browser/curl/openssl clients connecting to the NeutrinoOS web host:

- Key exchange: **X25519** (`key_share` group 0x001D), no HRR.
- Cipher suites: `TLS_AES_128_GCM_SHA256` (0x1301) and
  `TLS_AES_256_GCM_SHA384` (0x1302) — server preference 0x1301.
- Signature: **Ed25519** (`signature_algorithms` 0x0807) only.
- SNI: parsed and exposed (`Tls13Connection.ServerName`); a single
  wildcard-less certificate is served (SNI-based cert selection is a
  configuration extension, not needed in Phase 6).
- Middlebox-compat: dummy CCS is sent after ServerHello and ignored on
  receive.
- Alerts: close_notify handled; fatal alerts close the connection.

## Files

| File | Contents |
|------|----------|
| `Tls13.cs` | `Tls13Connection` state machine, record layer, key schedule (`DeriveSecret`/`ExpandLabel`), `TlsReader`/`TlsWriter` |
| `X509.cs` | DER writer, self-signed Ed25519 certificate generation, PEM encode/decode, targeted Ed25519 SPKI/seed extraction |

## Key schedule (verified against RFC 8448)

```
early_secret      = HKDF-Extract(0, 0^hashlen)
derived           = Derive-Secret(early_secret, "derived", Hash(""))
handshake_secret  = HKDF-Extract(derived, ECDHE)
c/s hs traffic    = Derive-Secret(handshake_secret, "c/s hs traffic", CH..SH)
derived2          = Derive-Secret(handshake_secret, "derived", Hash(""))
master_secret     = HKDF-Extract(derived2, 0^hashlen)
c/s ap traffic    = Derive-Secret(master_secret, "c/s ap traffic", CH..server Finished)
finished keys     = HKDF-Expand-Label(traffic, "finished", "")
```

Two implementation notes that cost real debugging time:

- The `"derived"` secrets hash the **empty transcript**, not the current
  one (checked against the RFC 8448 `derived` value
  `6f2615a108c702c5678f54fc9dbab69716c076189c48250cebeac3576c3611ba`).
- In TLS records the **length field includes the 16‑byte GCM tag**
  (`wire = 5 + length`, ciphertext is `length - 16`). Treating the
  length as ciphertext-only misparses the following record.

## Records

- AEAD per record: nonce = IV XOR (sequence number in the last 8 bytes),
  AAD = the 5‑byte record header, inner plaintext = `content ||
  content_type || zero padding`.
- Sequence numbers reset to 0 for each key epoch (handshake, application).
- Rekeying/KeyUpdate: not sent; a client KeyUpdate is answered with a
  fatal alert (documented limitation).

## X.509

Certificates are generated **on first webhost start** if
`/etc/ssl/certs/neutrinoos.crt` + `/etc/ssl/private/neutrinoos.key` are
missing:

- Self-signed v3, Ed25519, `CN=NeutrinoOS`, SANs:
  `dns:neutrinoos, dns:localhost, dns:neutrinoos.local, ip:127.0.0.1`,
  BasicConstraints (CA:FALSE), KeyUsage digitalSignature.
- Validity: 10 years from the RTC (`SysInfo.GetWallClock` via the raw
  pointer export).
- The private key is PKCS#8 (`X509.BuildPkcs8Ed25519`) written as PEM —
  readable by OpenSSL.
- Loading accepts PEM or raw DER for the certificate and PKCS#8 PEM for
  the key (`FromPem`, `PeelEd25519Seed`); the SPKI is cross-checked with
  `PeelEd25519Public`.

The DER writer emits minimal-form lengths; the two structure bugs found
during bring-up are fixed and regression-covered by the offline harness
`build/p6-x509test.sh` (compiles the real `X509.cs` on Linux and
validates with `openssl x509` / `python-cryptography`).

## Client interop facts (debugged end-to-end)

- OpenSSL 3.0 (`curl`) accepts the handshake: full request/response over
  TLS 1.3 verified (`https://…/health` → `{"status":"ok"}`, code 200).
- The capture scripts `build/p6-tls-proxy.py` + `build/p6-tlsparse.py`
  dissect the wire when something regresses.

## Limitations (deferred to Phase 7+)

- **TLS 1.2 is not implemented.** The four TLS 1.2 suites required by
  the spec (`TLS_ECDHE_{RSA,ECDSA}_WITH_AES_{128,256}_GCM_*`) need RSA or
  ECDSA signing, which Phase 6's crypto inventory does not include
  (Ed25519/X25519 only). Modern clients negotiate 1.3, which is
  implemented and verified.
- No 0‑RTT, no client certificates, no session tickets/resumption, no
  OCSP stapling, no HelloRetryRequest, no TLS_CHACHA20_POLY1305_SHA256
  (the AEAD primitive exists; the suite wiring is a small follow-up).
