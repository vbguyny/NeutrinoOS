# PHASE6-REPORT.md — Phase 6 implementation report

Scope: TCP/IP completion, crypto stack, TLS 1.3, X.509, SSH server,
users, web hosting, firewall, boot integration, tests and docs.

## 1. Changes by area

### Crypto (`src/ddk/Crypto/`)
| Item | Detail |
|------|--------|
| `Csprng` | ChaCha20-based DRBG seeded from RTC/HPET/TSC/interrupt timing; `Kernel_GetEntropy` export wired through the DDK |
| `Sha256`/`Sha512` | streaming + one-shot, KAT-verified |
| `Hmac` | RFC 2104; used by TLS (`hkdf-expand-label`) and SSH (`hmac-sha2-256/512`, incl. `-etm`) |
| `Hkdf` | extract/expand + `HKDF-Expand-Label` |
| `Scrypt` | N=16384, r=8, p=1 — password storage |
| `X25519` | Montgomery ladder, RFC 7748 vectors |
| `Ed25519` | RFC 8032 sign/verify (SHA-512 backed) |
| `Aes`, `Gcm` | AES-128/256 core + GHASH/GCM (NIST vectors) |
| `Curve25519` helpers | used by crypto tests utility |

### TLS 1.3 + X.509 (`src/ddk/Tls/`)
- `Tls13.cs`: full server handshake — ClientHello parse, X25519 key
  share, `HKDF` schedule per RFC 8448, AES-128-GCM records, Ed25519
  CertificateVerify, Finished exchange; app-data loop with
  `SendAll`-style partial-write handling; graceful close.
- `X509.cs`: DER writer + self-signed v3 Ed25519 certificate
  (BasicConstraints, KeyUsage, SAN), PEM read/write, PKCS#8 private key.

### SSH server (`src/ddk/Ssh/`)
- `SshService`/`SshConnection`: version exchange, KEX
  (`curve25519-sha256`), host key (`ssh-ed25519`), AES-CTR + HMAC
  (incl. ETM), user auth (password via scrypt, publickey via
  authorized_keys), session channels, `exec` with exit status, pty
  interactive shell (line editing, history, tab completion via the
  shared shell front-end).
- Host key auto-generated on first start to
  `/etc/ssh/ssh_host_ed25519_key` (+`.pub`).

### Users (`src/ddk/Users/UserDatabase.cs`)
`/etc/passwd`, `/etc/shadow` (scrypt), `~/.ssh/authorized_keys`;
root denied for remote auth.

### Web (`src/ddk/Services/WebService.cs`, `src/utilities/webhost/`)
HTTP/1.1 (keep-alive) on :80, TLS 1.3 on :443, routes `/`, `/health`,
`/time`, static `/var/www` with traversal rejection, access log.
Config `/etc/webhost.conf`; certs under `/etc/ssl/`.

### Firewall (`src/ddk/Network/Firewall.cs`)
First-match rules (`deny|allow ip|tcp|all`), hook in
`TcpListener.HandleIncomingSyn`, `/etc/firewall.conf`, logging.

### Boot integration (`src/kernel/Shell/ShellInit.cs`)
`/etc/boot.params` (`net.ip=dhcp|static`, `net.static.*`,
`sshd.autostart`, `webhost.autostart`) applied at shell init;
`/etc/rc.local` executed after profiles. `ifconfig eth0 static …`
added.

## 2. Verified outcomes (evidence)

| Check | Evidence |
|-------|----------|
| SSH E2E | `p6-ssh-test.sh`: pubkey exec rc=0, piped shell, negative auth rc≠0, password auth via pty driver; no `SYSTEM HALTED` |
| HTTPS | `curl -k https://…/health` → `{"status":"ok"}` (HTTP code 200) after TLS 1.3 handshake |
| X.509 | `openssl x509 -text` parses; dates/subject/extensions correct |
| Crypto KATs | on-device `cryptotest` utility (SHA/HMAC/AES/GCM/X25519/Ed25519/Scrypt vectors) |
| Firewall | 443 SYN denied + logged, 80 still served |
| Boot params | DHCP + autostart reachable with zero console input |
| Build | `p5-all.sh`: kernel rebuild + 36 utilities, deploy OK |

## 3. Bugs found and fixed during bring-up

1. **CRLF in shell output** broke SSH client expectations — normalize
   V_S/CRLF handling.
2. **OpenSSH KDF detail** — exchange hash `H` and `session_id` are
   hashed as raw bytes, not mpints (only `K` is an mpint).
3. **MAC key length** — per-direction key = negotiated MAC length
   (SHA-256 → 32 bytes), independent of cipher key size.
4. **ETM framing** — encrypt-then-MAC uses plaintext packet length in
   the MAC input and 16-byte alignment on the ciphertext.
5. **first_kex_packet_follows** — drop the guessed packet only when
   the client's first kex algorithm differs from the negotiated one.
6. **TLS record length includes the GCM tag** — `ctLen = recLen − 16`
   (off-by-a-tag bug produced "record decrypt failed").
7. **Handshake buffer reset** — clear the accumulated-CH buffer after
   ClientHello so the encrypted Finished record is parsed as a new
   handshake message (previously Finished was mis-parsed and the
   handshake stalled).
8. **X.509 structure** — three DER fixes: signature AlgorithmIdentifier
   needs the Ed25519 OID sequence, validity position within TBSCert
   fields, and SPKI algorithm identifier.
9. **VirtioNet under VirtualBox** — the transitional `1AF4:1000` device
   exposes the modern interface: `Initialize()` now tries the modern
   capability path first; 8-byte MMIO stores to the BAR raise a VBox
   guru, so queue desc/avail/used are written as two 32-bit stores
   (and the virtio-blk capacity is read as two 32-bit reads).
10. **Ethernet padding** — VBox pads short frames to 60 bytes; the
    stack now derives L4 lengths from the IP total-length field, and
    the driver pads TX frames to 60 bytes (both harmless under QEMU).
11. **Cold ARP cache** — `BuildIPv4Frame` silently dropped unicast IP
    packets when the cache had no next-hop entry (QEMU images warmed
    it via boot tests; the GUI image did not). Inbound IPv4 frames now
    glean the sender MAC into the cache, and cache misses send an ARP
    request before dropping. This fixed SSH/HTTP entirely under VBox.
12. **/time formatting in service context** — deep JIT formatting
    produced corrupt digits; solved by bridging to the proven `date`
    utility output through `ShellBridge` and assembling the string
    with chars.

## 4. Deviations / deferred (documented per feature)

- TLS 1.2 not enabled (needs RSA/ECDSA); TLS 1.3 is complete.
- Windows Schannel clients cannot negotiate Ed25519 (no EdDSA sig
  algs); verified OpenSSL-based clients and covered in the test runner
  by checking HTTPS from WSL `curl` while Windows `curl.exe` handles
  HTTP. Adding ECDSA P-256 makes Schannel work (Phase 7).
- Kestrel not ported; fallback in-DDK web server used (spec allows).
- RSA host keys and `rsa-sha2-*` deferred with RSA crypto.
- `/dev/random` device node deferred; CSPRNG API is the entropy path.
- SFTP, zlib, keyboard-interactive deferred; ChaCha20-Poly1305
  available for TLS only (SSH wiring deferred).
- Static IP via `ifconfig`/boot params is supported; DHCP remains the
  default and is what the tests use.

## 5. Phase 7 candidates

1. RSA (PKCS#1 v1.5 + PSS) and ECDSA P-256 → TLS 1.2, RSA host keys.
2. Kestrel transport port (pipelines/thread-pool adapters).
3. `/dev/random` + `/dev/urandom` character devices over the CSPRNG.
4. SSH SFTP subsystem + scp client compatibility pass.
5. Longer soak tests for the web server (connection exhaustion,
   partial reads) and firewall rule fuzzing.
