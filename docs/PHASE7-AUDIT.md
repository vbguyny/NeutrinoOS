# NeutrinoOS Phase 7 — Security Audit Notes

Companion to `docs/PHASE7-SECURITY.md` (model + mitigations). This file
records what was actually audited in Phase 7, what was found, and what
was changed. It is an internal audit, not an external one.

## 1. Cryptographic verification

- Known-answer tests: `build/p6-crypto-test.sh` runs the in-guest
  `cryptotest` utility over the NIST/RFC vectors — **30 PASS / 0 FAIL**
  (SHA-2, SHA-3, HMAC, AES-GCM, ChaCha20-Poly1305, Curve25519,
  Ed25519, RSA-PSS, ECDSA; details in `docs/PHASE6-CRYPTO.md`).
- Secret-dependent comparisons audited and confirmed XOR-accumulate
  (constant-time in iteration count):
  - `AesGcm.Open` — tag compare `diff |= expect[i] ^ tag[i]`
  - `ChaCha20Poly1305.Open` — same pattern
  - `SshCrypto.VerifyMac` — same pattern
  - `Scrypt.VerifyPassword` — compares scrypt outputs with
    `diff |= got[i] ^ want[i]`
- CSPRNG (`src/ddk/Crypto/Csprng.cs`): ChaCha20 keyed from kernel
  entropy (TSC/HPET/RTC jitter), SHA-256 re-keying per reseed, block
  counter advances per output. Not locked — callers are the
  cooperative single-threaded services (documented in the source).
  Residual caveat (from Phase 6): no dedicated hardware entropy.

## 2. Kernel syscall interface

- **Pointer validation** — every syscall that writes into a
  user-supplied buffer now range-checks (overflow-safe) against the
  canonical user window via `UserAccess.Valid` and returns `-EFAULT`
  before touching memory: `uname`, `sysinfo`, `clock_gettime`,
  `clock_getres`, `gettimeofday`, `getcwd`, `stat`, `fstat`,
  `getrandom`. Found during audit: these previously accepted any
  non-null pointer, letting ring 3 direct kernel writes at kernel
  addresses or wrapping ranges.
- **Length/index validation** — audited existing checks: `getrandom`
  caps length; `read`/`write` validate fd + count; `mmap` bounds its
  region; `grow`/`brk` paths update `HeapEnd` monotonically. No integer
  overflow paths found beyond the ones above (the range check uses an
  overflow-safe comparison).
- **Syscall filter** — added (`SYS_SET_SYSCALL_FILTER` = 500). Enforced
  centrally in `SyscallDispatch.Dispatch` (EPERM on cleared bits);
  mask inherits across `fork`; tighten-only; the installer syscall is
  exempt from its own filter. Ring-3 test 57 verifies install, allow,
  deny, and tighten-can't-loosen.

## 3. Physical memory allocator

- Bitmap allocator; audit findings:
  - `FreePage` on an already-free page was silently a no-op → now
    detects and reports double frees (always-on check; console report
    gated by `PoisonOnFree` to stay quiet on release boots).
  - Added optional poison-on-free (`0xDD`) for debug builds.
  - `AllocatePage` skips page 0; kernel-range allocations/frees warn on
    the console (pre-existing instrumentation, kept).
- Page-table hygiene: found and fixed a **self-test bug** where the
  Phase 7 security self-test created a scratch user address space and
  destroyed it — `CreateUserSpace` shares `PML4[0]` and
  `PML4[256..511]` with the kernel, so the destroy freed page-table
  pages still in use (observed as a boot stall after shell start; the
  free-list pages were later reallocated, corrupting low-identity
  mappings). The self-test now walks the live tables without
  allocating. `DestroyUserSpace` itself is only safe for spaces whose
  user-half subtables are process-private — the pre-existing users
  (fork cleanup) satisfy this.

## 4. W^X and execution permissions

- Kernel: identity-map pages outside the image are NX; the image is
  re-mapped executable by `ProtectKernelImage` (2 MB pages). Verified
  each boot: `[SEC] PASS kernel image is executable (W^X)` +
  `[SEC] PASS ordinary RAM is NX (W^X)`.
- User: code = `Present|User` (RX), data/stack = `Present|User|Writable|NoExecute`.
- JIT code in the kernel world executes from the executable image span
  (documented model; the JIT arena is part of the kernel image range).

## 5. Network-facing audit

- SSH algorithm inventory (code-verified, see `SshConnection`):
  curve25519-sha256 KEX; ssh-ed25519 host/user keys; aes128/256-ctr;
  hmac-sha2-256/512 (+etm). No weak primitives present to negotiate.
- SSH auth paths hardened (lockout per IP, per-connection cap, audit
  log; password auth default off; root refused).
- Web host: per-IP 4-connection cap, 10-connection global cap, 30
  req/s/IP and 20 req/s/connection limits → 429 + close. Request
  parsing bounds are pre-existing (12,288-byte input buffer, header/
  body length checks).
- TLS: 1.3-only implementation; certificate handling self-signed per
  install; verification of the *peer's* certificates is a client-side
  concern (the `curl`/`ssh` utilities are the clients; `-k` documented
  for self-signed labs).

## 6. Findings summary

| # | Severity | Finding | Status |
|---|----------|---------|--------|
| F1 | High | Syscall write-targets accepted arbitrary user pointers (kernel-write primitive) | **Fixed** — UserAccess range checks |
| F2 | High | Security self-test freed shared kernel page tables at boot (corruption) | **Fixed** — walk-in-place |
| F3 | Medium | No brute-force protection on SSH auth | **Fixed** — per-IP lockout + audit |
| F4 | Medium | Web host had no request/connection limits | **Fixed** — 429 + caps |
| F5 | Medium | Freed pages silently double-free tolerated | **Fixed** — detection + optional poison |
| F6 | Low | Password auth enabled by default (Phase 6 behavior) | **Fixed** — default off, explicit config |
| F7 | Low | No audit trail for password/account changes | **Fixed** — auth.log events |
| F8 | Info | Executable base not randomized | Documented residual (no reloc loader) |
| F9 | Info | No AOT stack canaries | Documented residual (W^X/NX mitigate) |
| F10 | Info | auth.log unbounded | Documented; manual truncate |
| F11 | Medium | webhost pinned connection-table slots until the 12s idle timeout on short-lived connections (burst → refusals) | **Fixed** — peer-EOF slot reclaim |
| F12 | Low | auth.log never created: single-level `Directory.CreateDirectory` failed on missing `/var` (exception swallowed) | **Fixed** — stepwise /var then /var/log + image fixture dirs |
| F13 | Low | Windows schannel TLS clients rejected: NeutrinoOS TLS 1.3 server requires a signature_algorithms entry it recognizes; schannel's ClientHello matches no offered algorithm (server logs `hello rejected ... sig=0`). OpenSSL (WSL curl, curl, browsers using BoringSSL) negotiate fine | **Documented** — use an OpenSSL-based client; schannel interop deferred. Found during the VirtualBox acceptance (Windows `curl.exe`) |
| F14 | High | 2-vCPU boots deadlocked (QEMU `-smp 2` and VirtualBox): early AP startup let a trampoline-era fault enter exception handling before JIT/EH/console lock ordering existed; `ExceptionHandling._lock` ended held with both CPUs spinning (`pause`/`atomic_cmpxchg32`) | **Fixed** — APs start only from `Kernel.Main` after early init (moved out of Arch Stage 2); APs additionally park in `SMP.ApEntry` until `ReleaseAps()` |

## 7. How to re-run the audit evidence

```bash
bash build/p6-crypto-test.sh        # crypto KATs (30/30)
bash build/p7-bootloop.sh 3         # [SEC] lines + Test 57 in /root/bootloop.log
bash build/p7-ssh-lockout.sh        # lockout + auth.log
bash build/p7-web-ratelimit.sh      # HTTP 429
make reproducible                   # supply-chain determinism
```
