# PHASE6-ACCEPTANCE.md — step-by-step verification (Windows 11)

Prerequisites: Windows 11 + WSL2 Ubuntu‑24.04 with the Phase 1 toolchain
(.NET 10, bflat, QEMU/OVMF). All commands below are run from a normal
PowerShell window; the WSL-side scripts are the Phase 6 test harness.

## One-shot verification

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-phase6-tests.ps1
```

This runs, in order: managed crypto KATs in QEMU, the SSH end-to-end
suite, the web/TLS suite (with boot-parameter autostart), the firewall
test, then Windows-side checks against a live VM using the **Windows**
`ssh.exe` and `curl.exe`.

Individual suites (from WSL, `wsl -d Ubuntu-24.04 -u root`):

```bash
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-crypto-test.sh     # KATs
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-ssh-test.sh       # SSH E2E
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-web-test.sh       # HTTP+TLS
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-firewall-test.sh  # firewall
```

Interactive session from Windows:

```powershell
scripts\phase6-ssh-demo.ps1            # expects a running VM on :2222
wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-qemu-serve.sh
```

## Checklist status

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Boot with no Phase 5 regressions | ✅ | `build/p5-all.sh` + QEMU boot; shell banner, profiles, history unchanged |
| 2 | `ifconfig` shows DHCP-assigned IP | ✅ | `[NetMgr] eth0 configured: 10.0.2.15` in every suite log |
| 3 | Guest → host `ping` | ✅ | `build/p6-web-test.sh` runs `ping -c 2 10.0.2.2` over the SLIRP host |
| 4 | `sshd` starts; host can `ssh` in | ✅ | `p6-ssh-test` phases 2–5; Windows client via `run-phase6-tests.ps1` |
| 5 | Password authentication | ✅ | OpenSSH password login against `/etc/shadow` (scrypt, password `neutrino` for `user`); driven by `build/p6-ssh-pw.py` in phase [7/7] of `p6-ssh-test.sh` (rc=0, `PW-OK`); root refused by policy |
| 6 | Public-key authentication | ✅ | `authorized_keys` flow, including the `PK_OK` query and signed blob verification |
| 7 | Interactive shell over SSH (editing/history/tab) | ✅ | `p6-ssh-test` `-tt` session shows shell banner, prompt and `exit`; line editor + history implemented |
| 8 | `exec` + exit codes | ✅ | `echo`/`uname` return rc=0; wrong command exits non-zero |
| 9 | .NET 10 web app hosting | ⚠️ fallback | `webhost` starts the built-in HTTP/1.1 + TLS server; the Kestrel port is Phase 7+ (see PHASE6-WEB.md). Template provided |
| 10 | HTTPS with self-signed cert (`curl -k`) | ✅ | `code=200` over TLS 1.3; cert auto-generated (Ed25519). Note: Windows `curl.exe` uses Schannel, which does not offer Ed25519 — use WSL `curl -k` or a browser |
| 11 | Static files from `/var/www` | ✅ | `GET /hello.txt` → `static file from /var/www` |
| 12 | Host key generation on first boot | ⚠️ partial | `ssh_host_ed25519_key` (+`.pub`) generated; RSA host key deferred with RSA crypto |
| 13 | RNG + `/dev/random` | ⚠️ partial | CSPRNG seeded from RTC/HPET/TSC/interrupts (`Kernel_GetEntropy`); no `/dev/random` device (services call the CSPRNG directly) |
| 14 | Packet filter by port/source | ✅ | `p6-firewall-test` denies 443, allows 80; logs the denied SYN |
| 15 | No C/C++ files in the new trees | ✅ | all Phase 6 code is C#; only build scripts (bash/python) outside the source trees |
| 16 | Prior docs still valid + this document | ✅ | Phase 1–5 docs unchanged; this file covers the Phase 6 items |

## Manual spot checks

```powershell
# crypto KATs on device
ssh -i $env:TEMP\neutrinoos-p6key -p 2222 user@localhost "cryptotest"

# note: use 127.0.0.1, not localhost (resolves to ::1 first on Windows)
ssh -i $env:TEMP\neutrinoos-p6key -p 2222 -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL user@127.0.0.1 "uname"
curl.exe -s http://127.0.0.1:8080/health
wsl curl -sk https://127.0.0.1:8444/health        # HTTPS (Schannel lacks Ed25519)

# boot parameters (guest console):
#   cat /etc/boot.params    -> net.ip=dhcp / webhost.autostart=yes
#   cat /etc/firewall.conf  -> deny tcp 443 (when present)

# HTTPS certificate inspection (from the capture of a live handshake):
openssl s_client -connect localhost:8444 -tls1_3 -brief     # after VBox/QEMU runs
```

## Known deviations (accepted for Phase 6)

- Kestrel/ASP.NET hosting is replaced by the fallback server (spec
  explicitly allows this; see PHASE6-WEB.md).
- TLS 1.2 omitted (requires RSA/ECDSA — Phase 7).
- RSA host keys / `rsa-sha2-*` deferred with RSA crypto.
- `/dev/random` device deferred; CSPRNG API is the entropy interface.
- SFTP subsystem, zlib compression, SSH `keyboard-interactive` and
  ChaCha20‑Poly1305 wiring are deferred (documented per feature).
- Windows Schannel-based clients (`curl.exe`, .NET `HttpClient`) do not
  offer Ed25519 signature algorithms, so they cannot complete the
  Ed25519 TLS 1.3 handshake; OpenSSL/BoringSSL/NSS clients (WSL `curl`,
  browsers) work. ECDSA P-256 support is the Phase 7 fix.
