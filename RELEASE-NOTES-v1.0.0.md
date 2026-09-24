# NeutrinoOS v1.0.0 Release Notes

Released: 2026-09-24 · Codename: "Phase 7" — performance, security, packaging

NeutrinoOS is a console-only, headless operating system that runs .NET 10
applications on bare metal: a custom x86-64 kernel (AOT-compiled C#) with
a Tier-0 JIT that executes unmodified .NET assemblies, a POSIX-flavored
syscall interface, FAT32/AHCI storage, virtio-net networking, TLS 1.3,
an SSH server, and a web host.

## Highlights in 1.0.0

### Performance (all measured, see `docs/PHASE7-PERF-RESULTS.md`)

| Area | Before → After |
|------|----------------|
| FAT32 sequential write (1 MB) | 5.9 s → 0.63 s (**9.3x**) |
| Loopback TCP (4 MB) | 11.2 s → 0.97 s (**11.6x**, 4.24 MB/s) |
| Serial output during boot | 11,696 → 5,895 lines/boot |
| Boot test timeline (dev image) | **3.1x** faster |
| Debug (dev) image time-to-prompt | ~41 s → ~18 s |
| TLS handshake latency | −29% |
| SSH session setup | −25% |
| Gen-0 GC pause | 43 ms (5.8x throughput) |

JIT compilation is 2.8x faster on the standard workload; kernel W^X
re-mapping and per-thread kernel stacks landed as part of the hardening.

### Security

- **W^X**: kernel image executable, all other RAM NX; user pages
  read-execute or read-write-NX — verified every boot by a security
  self-test (`[SEC] 4 pass, 0 fail`).
- **ASLR**: user stack (256 MB window), heap + mmap bases (64 MB
  windows) randomized per process.
- **Syscall filtering**: `sys_filter_install` (syscall 500) installs a
  512-bit allow-mask; inherited across fork, tightenable-only.
- **User-pointer validation** on all kernel-write syscall targets.
- **sshd**: per-IP brute-force lockout, per-connection attempt cap,
  `/var/log/auth.log` audit trail, password auth **off by default**
  (public-key only), root login refused over SSH.
- **webhost**: per-IP connection cap + request rate limiting (HTTP 429).
- **Allocator audit**: double-free detection; optional poison-on-free.
- Modern-only SSH/TLS cryptography (curve25519, Ed25519, aes-ctr,
  hmac-sha2; TLS 1.3).

### Release packaging

- **Reproducible builds**: `make reproducible` proves byte-identical
  images across two clean builds (pinned `SOURCE_DATE_EPOCH` + FAT
  volume serial; see `toolchain.lock`).
- **Artifacts**: raw `.img`, `.qcow2`, VirtualBox `.ova` (via
  `scripts/build-ova.ps1`), SHA-256 checksums, optional GPG signature,
  machine-readable `release.json`.
- **Windows 11 installers**: Hyper-V script
  (`scripts/install-neutrinoos.ps1`), USB writer
  (`scripts/flash-usb.ps1`), and the OVA import flow
  (`docs/PHASE7-INSTALL-WINDOWS.md`).
- `/etc/neutrinoos-release` identifies the release inside the guest.

## Compatibility

- x86-64 UEFI systems (QEMU + OVMF, VirtualBox 7.x EFI, Hyper-V Gen-2).
- .NET 10 (RC-generation IL; standard console assemblies).
- NIC: virtio-net (QEMU/VBox), Intel E1000 (VBox fallback).

## Known limitations (see `docs/PHASE7-SECURITY.md` §3)

- Executable image base is not randomized (stack/heap/mmap are).
- No stack canaries in AOT code (W^X + NX stacks mitigate).
- Password auth remains available when explicitly configured.
- No log rotation (auth.log grows; truncate manually).
- Single-user OS: no multi-tenant isolation guarantees.

## Getting started

1. Install artifacts + verify checksums (`docs/PHASE7-RELEASE.md`).
2. Follow `docs/PHASE7-INSTALL-WINDOWS.md` (VirtualBox OVA or Hyper-V).
3. Boot; you land in the NeutrinoOS shell on the serial/VGA console.
4. Try: `dhcp`, `sshd`, `webhost`, `uname -a`, `df`, `ls /bin`.

## Credits

Built with bflat (local ILCompiler fork), .NET 10, LLVM/LLD 18, GNU
mtools, QEMU/OVMF. See `toolchain.lock` for exact versions.
