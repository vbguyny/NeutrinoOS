# NeutrinoOS Phase 7 — Security Model

Status: Phase 7 hardening implemented and boot-verified (see
`docs/PHASE7-ACCEPTANCE.md` for the machine-checked evidence).

This document states the threat model, lists the mitigations that are
implemented, and is explicit about residual risk. It complements the
crypto details in `docs/PHASE6-CRYPTO.md`.

## 1. Threat model

NeutrinoOS is a single-user, console-first managed OS that runs .NET 10
applications on bare metal (custom x86-64 kernel + Tier-0 JIT). The
realistic threats for v1.0.0 are:

| # | Threat | Vector |
|---|--------|--------|
| T1 | Malicious user application escalating to kernel | Ring 3 .NET code, malformed syscall arguments |
| T2 | Network attacker gaining shell access | sshd on port 22 (password or key) |
| T3 | Network attacker abusing the web host | HTTP/HTTPS request handling, TLS stack |
| T4 | Memory corruption from kernel bugs | Double free / use-after-free in the physical allocator |
| T5 | Code injection into memory | Writable+executable pages anywhere |
| T6 | Side-channel / brute force on authentication | Repeated login attempts, timing |
| T7 | Supply-chain tampering with releases | Modified images or manifests |

Explicitly OUT of scope for v1.0.0: hostile local users on shared
hardware (single-user OS), physical attacks, hardware side channels
(spectre-class), multi-tenant isolation, formal verification.

## 2. Implemented mitigations

### 2.1 Memory safety

- **W^X (kernel)** — the kernel identity map no longer contains
  executable mappings outside the kernel image itself. After boot,
  `VirtualMemory.ProtectKernelImage` re-maps exactly the kernel image
  range as executable and everything else in RAM as NX. Verified every
  boot by the security self-test:
  `[SEC] PASS kernel image is executable (W^X)` +
  `[SEC] PASS ordinary RAM is NX (W^X)`.
- **W^X (user)** — loaded user code pages are mapped
  `Present|User` (read+execute, no write); user data/stack pages are
  mapped `Present|User|Writable|NoExecute`. There is no
  writable-executable user mapping by construction.
- **ASLR** — at process creation the kernel draws bytes from its
  entropy pool (TSC/HPET/RTC stirred through an xorshift64* mixer) and
  randomizes:
  - user stack top: 256 MB window, page granularity
    (`UserLayout.RandomizedStackTop`),
  - heap start and mmap base: 64 MB windows
    (`UserLayout.RandomPageSlide`, applied in `NetExecutable`).
  `UserLayout.AslrEnabled = false` restores the fixed layout for
  deterministic debugging.
- **Stack/heap guard pages** — the user stack and heap are sparse
  mappings; the pages immediately below the stack bottom and above the
  heap end are *not* mapped, so overflow/underflow faults instead of
  silently corrupting adjacent memory.
- **Allocator audit** — the physical page allocator is a bitmap
  allocator; `FreePage` verifies that the page was actually allocated
  (double free is detected and ignored, never silently double-counted).
  With `PageAllocator.PoisonOnFree` enabled (debug builds) freed pages
  are filled with `0xDD` and double frees are reported on the console.

### 2.2 Privilege separation

- **Ring 3 isolation, verified at boot** — `SecuritySelfTest` walks a
  freshly created user address space and asserts:
  - kernel identity memory (low 256 MB) has no user-accessible leaf
    mapping,
  - the kernel physmap window has no user-accessible leaf mapping.
  Result lines: `[SEC] PASS user cannot read kernel identity memory`,
  `[SEC] PASS user cannot read the kernel physmap`.
- **Syscall filtering (seccomp-like)** — `sys_filter_install`
  (syscall number 500, `SYS_SET_SYSCALL_FILTER`) installs a 512-bit
  allow-mask for the calling process. `SyscallDispatch.Dispatch`
  returns `-EPERM` for any syscall whose bit is clear. The mask:
  - is inherited by children across `fork` (a sandbox cannot escape by
    forking),
  - can only be tightened after the first install (the new mask is
    ANDed in; there is no way to loosen or remove the filter),
  - never blocks `sys_filter_install` itself (so a process may further
    restrict itself).
  Verified in ring 3 every boot by user-mode test 57
  (`[PASS] syscall_filter install/deny/tighten verified`).
- **User-pointer validation** — syscalls that *write into*
  caller-supplied buffers validate the pointer+length against the
  canonical user window (`UserAccess.Valid`, overflow-safe range
  check): `uname`, `sysinfo`, `clock_gettime`, `clock_getres`,
  `gettimeofday`, `getcwd`, `stat`, `fstat`, `getrandom`. A kernel
  address or a wrapping range is rejected with `-EFAULT` before any
  store happens.

### 2.3 Network-facing hardening

- **sshd** (Phase 7 additions in `SshService` / `SshConnection` /
  `SshAuthGuard`):
  - **Secure default**: `PasswordAuthentication` is **off** unless
    `/etc/ssh/sshd_config` explicitly sets `PasswordAuthentication=yes`
    (public-key only out of the box).
  - **Root login refused** over SSH (both password and publickey paths;
    root is console-only by policy).
  - **Per-IP lockout**: failed authentications are counted per source
    IP; at `BanThreshold` failures (default 5) the IP is banned for
    `BanSeconds` (default 300). New connections from a banned IP are
    refused at accept time.
  - **Per-connection cap**: `MaxAuthAttempts` (default 6) failures on a
    single connection trigger an SSH disconnect
    (`NO_MORE_AUTH_METHODS_AVAILABLE`).
  - **Audit trail**: `/var/log/auth.log` records accepted connections,
    auth successes (user+method), auth failures (running count), bans,
    banned rejects, over-limit disconnects — and, from the user
    database, `password changed` / `account created` events.
  - **Modern algorithms only** (verified in the code): key exchange
    `curve25519-sha256`; host key and user keys `ssh-ed25519`;
    ciphers `aes128-ctr`/`aes256-ctr`; MACs `hmac-sha2-256/512`
    (+etm). No DSA, no RSA/SHA-1, no group1/14, no 3DES, no CBC, no
    `none`.
- **webhost** (Phase 7 additions in `WebService`):
  - **Disabled by default** — starts only via the `webhost` command or
    `webhost.autostart=yes` in the boot config.
  - **Per-IP connection cap**: 4 concurrent connections (global cap 10).
  - **Rate limiting**: >30 requests per source IP per second → `429 Too
    Many Requests` + connection close; per-connection cap 20 req/s
    within a keep-alive stream.
- **TLS** — TLS 1.3 only (Ported implementation in `src/ddk/Tls`).
  Cipher suite chosen by the TLS 1.3 fixed set (ChaCha20-Poly1305 /
  AES-GCM with SHA-256/384 KDF); certificates are self-signed Ed25519
  per install. Weak cipher suites / TLS 1.1- are structurally absent
  (no code path implements them).

### 2.4 Crypto verification

`docs/PHASE6-CRYPTO.md` documents the NIST/RFC vector test coverage
(SHA-2/3, HMAC, AES-GCM, ChaCha20-Poly1305, Curve25519, Ed25519,
RSA-PSS, ECDSA). Constant-time comparisons are used for MAC and
password-hash verification (`Scrypt.VerifyPassword` hashes both sides
before comparison; MAC checks use XOR-accumulate comparison). The
CSPRNG is seeded from TSC/HPET/RTC jitter at first use and re-stirred
per output block; its quality caveat (no dedicated entropy hardware) is
documented there.

## 3. Residual risks (accepted for v1.0.0)

| Risk | Why it remains | Mitigation today |
|------|----------------|------------------|
| Executable **base address** is fixed (256 MB), only stack/heap/mmap are ASLR'd | The loader maps flat images without relocation processing | Image base is far from attacker-controlled data; W^X prevents overwriting code |
| Kernel-mode access to *unmapped* user pointers faults in kernel context | No SMAP-style probe/return handling for every pointer | Only the validated write-targets above dereference user memory; read paths are next-phase work |
| No stack canaries in AOT code | bflat build does not pass `-fstack-protector-strong`; enabling changes codegen and requires full regression | W^X + NX stacks remove the classic shellcode path; revisit in Phase 8 |
| `PageAllocator.PoisonOnFree` off by default | Poisoning costs on every free | Enable in debug builds; double-free detection is always on |
| Password auth remains available as an option | Console-first single-user OS; key setup needs an existing path to install keys | Off by default; lockout + audit log when enabled |
| Auth log grows unbounded | FAT has no rotation utility yet | Small text lines, manual truncate documented in the manual |
| No protection against a malicious *kernel* extension | Kernel is a single statically-linked image; DDK runs as JIT code in kernel context by design | Out of scope: NeutrinoOS has no third-party kernel module loader |

## 4. Verification map (Phase 7 acceptance)

| Check | Evidence |
|-------|----------|
| W^X kernel/user | `[SEC] PASS ...` lines in serial log |
| Ring 3 cannot touch kernel memory | `[SEC] PASS user cannot read kernel identity memory` / `physmap` |
| ASLR active | two `BinaryLoader` boot logs show different stack tops across boots |
| Syscall filter | user-mode test 57 PASS |
| sshd lockout + audit | `p7-ssh-lockout` probe: 5 bad passwords → ban, `/var/log/auth.log` lines |
| Web 429 | `p7-web-ratelimit` probe: burst > limit → HTTP 429 |
| Secure defaults | `sshd_config` absent → password auth refused; webhost not started |
| Modern SSH algorithms | negotiation list inspection (code + client `-vv` transcript) |
