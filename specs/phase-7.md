# ROLE

You are a senior systems engineer specializing in bare-metal performance
profiling, kernel security hardening, managed runtime optimization, and
release engineering for custom operating systems. You are assisting in
Phase 7 of a custom operating system project.

# PROJECT CONTEXT

Project name: NeutrinoOS
Base project: ProtonOS (a managed OS written entirely in C# using bflat's
  zero-library mode, with a Tier-0 JIT compiler).

Phase 1 status: COMPLETE. Graphics/framebuffer/GOP removed. Serial console
  at COM1 (0x3F8, 115200 8N1).

Phase 2 status: COMPLETE. UART 16550 driver (`/dev/ttyS0`) with
  interrupt-driven RX/TX. Line discipline with canonical/raw modes,
  editing, Ctrl+C/D/U, 32-entry history. Console Abstraction Layer (CAL)
  with `IConsoleDevice` and `ConsoleMultiplexer`. `korlib` implements
  `System.Console`, `System.IO.TextWriter`/`TextReader`,
  `System.ConsoleColor`, `System.ConsoleKey`,
  `System.Text.Encoding.UTF8`, `System.Environment`.

Phase 3 status: COMPLETE. VGA text-mode driver (`/dev/vga0`, 80x25/80x50,
  ANSI parser, CP437). PS/2 keyboard driver (IRQ1, scancode set 1, key
  repeat, modifier tracking). CAL routes output to both consoles and
  switches active input automatically.

Phase 4 status: COMPLETE. Tier-0 JIT validates .NET 10 assemblies
  (C# 14). `korlib` expanded with `System.IO`,
  `System.Collections.Generic`, `System.Linq`, `System.Threading`,
  `System.Threading.Tasks` (synchronous minimal), `System.Text`,
  `System`, `System.Net`, `System.Globalization`,
  `System.Diagnostics`. Cross-assembly loading with
  `AssemblyLoadContext`. Standard .NET 10 console applications run on
  NeutrinoOS.

Phase 5 status: COMPLETE. Production shell with tokenizer, parser, pipes
  (`|`), redirection (`>`, `>>`, `<`, `2>`, `2>>`), background execution
  (`&`), sequential (`;`) and conditional (`&&`, `||`) execution.
  Built-in commands and external utilities (`.dll`, .NET 10) including
  `ls`, `cat`, `echo`, `mkdir`, `rm`, `cp`, `mv`, `wc`, `grep`, `ps`,
  `kill`, `sleep`, `df`, `mount`, `umount`, `uname`, `date`, `uptime`,
  `free`, `env`, `ifconfig`, `dhcp`, `ping`, `dns`, `netstat`, `wget`,
  `curl`, `ssh` (client), `gc`, `history`, `export`, `unset`. Shell
  initialization with `/etc/profile`, `PS1` customization, persistent
  history, basic tab completion.

Phase 6 status: COMPLETE. Hardened TCP/IP stack with POSIX-like socket
  API. Managed C# cryptographic primitives (SHA family, HMAC, AES-GCM,
  ChaCha20-Poly1305, ECDH, Curve25519, RSA, ECDSA, Ed25519, CSPRNG).
  TLS 1.2/1.3 implementation. SSH-2.0 server (sshd) with password and
  publickey authentication, PTY-backed shell sessions, and
  `exec`/`subsystem` requests. Minimal user database (`/etc/passwd`,
  `/etc/shadow`, home directories, `authorized_keys`) with modern
  password hashing. .NET 10 web hosting via ported Kestrel (or fallback
  HTTP/1.1 server) with HTTP and HTTPS support, static file serving
  from `/var/www/`. Kernel boot parameters for network configuration
  and service autostart. `/dev/random`. Minimal packet filter.

Target of the overall project: A console-only, headless managed OS that
  runs .NET 10 console applications and utilities on bare metal, with
  TCP/IP networking, SSH, curl, and the ability to host .NET web apps.
  No GUI, no graphical framebuffer, no window manager.

# ENVIRONMENT

- Host OS: Windows 11 (x64)
- IDE: Visual Studio Code (latest stable) with the Remote - WSL extension
- Shell: PowerShell 7 on the host; bash inside WSL2 Ubuntu 24.04
- Toolchain (already installed in WSL2 from Phase 1):
  - .NET SDK 10.0 (with C# 14)
  - bflat (configured to target .NET 10)
  - clang / ld.lld (LLVM 17+)
  - GNU make
  - Python 3.11+
  - qemu-system-x86_64 with OVMF firmware
  - git
- Phase 6 boot verification used:
  `make run-qemu-vga` (boots to NeutrinoOS shell on serial and VGA).
  `tests/run-phase6-tests.ps1` verifies SSH server, web hosting, and
  network services.
- Windows 11 host has VirtualBox 7.x installed for manual verification.
- Phase 7 introduces performance profiling, security auditing, and
  release packaging. The design must not introduce new native
  dependencies; all tooling must run in WSL2 or PowerShell 7.

# PHASE 7 GOAL — "PERFORMANCE OPTIMIZATION, SECURITY HARDENING, AND RELEASE PACKAGING"

Transform NeutrinoOS from a functionally complete system into a
production-ready, distributable operating system. Phase 7 delivers
three integrated capabilities:

1. **Performance optimization**: Profile the kernel, JIT compiler, GC,
   scheduler, network stack, and SSH/TLS paths. Eliminate bottlenecks,
   reduce boot time, and improve throughput and latency for .NET 10
   applications and network services.

2. **Security hardening**: Audit the entire system for memory-safety,
   privilege-escalation, and network-facing vulnerabilities. Implement
   mitigations (W^X, ASLR, stack canaries, syscall filtering, secure
   defaults for services, rate limiting, audit logging).

3. **Release packaging**: Produce installable, versioned release artifacts
   for VirtualBox (`.ova` / `.vdi`), QEMU (`.qcow2` / `.img`), and raw
   disk images. Provide a signed release manifest, reproducible builds,
   and a one-command Windows 11 installer script.

Phase 7 is complete when NeutrinoOS can be installed from a release
artifact on a fresh Windows 11 machine, boots in VirtualBox with the
default NIC, and serves a .NET 10 web app over HTTPS with performance
and security suitable for a production deployment.

Phase 7 does NOT include: a graphical installer, a package manager,
multi-architecture support (ARM64, RISC-V), or a full security audit
by an external party. It is strictly about optimization, hardening,
and packaging for the x86-64 platform.

# DETAILED TASKS

## Task 1 — Performance profiling infrastructure

Build the tooling required to measure performance before optimizing.

- **Kernel-level profiling**:
  - Add a high-resolution timer API (`System.Diagnostics.Stopwatch`
    backed by the TSC or HPET) to `korlib`.
  - Add a kernel profiling hook that samples the instruction pointer
    (via the APIC timer or NMI) and records samples in a ring buffer.
  - Expose the ring buffer via a `/dev/profiler` character device so a
    user-mode profiler can read it.
  - Add a `perf` shell command that starts, stops, and dumps profiling
    samples (top functions by sample count, with symbol names from the
    AOT and JIT symbol tables).
- **JIT profiling**:
  - Add instrumentation to the Tier-0 JIT to record compile time per
    method, IL size, native code size, and inline decisions.
  - Expose JIT statistics via a `jitstats` shell command.
- **GC profiling**:
  - Instrument the compacting GC to record collection count,
    pause time, heap size before/after, and LOH size.
  - Expose GC statistics via a `gcstats` shell command.
- **Network profiling**:
  - Add per-socket counters (bytes in/out, packets in/out, retransmits,
    connection count, connection duration).
  - Expose counters via `netstat -s` and a `/dev/netstats` device.
- **Boot profiling**:
  - Add timestamped boot markers at each kernel init stage.
  - Add a `boottime` shell command that prints the boot timeline.
- **Benchmark suite**: Create `tests/benchmarks/` with .NET 10 console
  applications that measure:
  - JIT compile throughput (methods/second).
  - GC allocation and collection throughput (MB/s, collections/s).
  - Scheduler context-switch latency (μs).
  - TCP throughput (loopback and VirtIO-Net, MB/s).
  - TLS handshake latency (ms).
  - SSH session setup latency (ms).
  - File I/O throughput (MB/s) on FAT32 and EXT2.
- Document the profiling infrastructure in
  `docs/PHASE7-PROFILING.md`.

## Task 2 — Performance optimization

Use the profiling data from Task 1 to optimize the system. Focus on
the areas with the largest measured impact.

- **Boot time**: Target a 30% reduction in time-to-shell. Likely wins:
  - Parallel driver initialization where dependencies allow.
  - Lazy initialization of subsystems not needed before the shell
    (e.g., SSH host key generation, web server).
  - Deferred GC initialization until the first allocation.
  - Faster page-table setup (use 2 MB huge pages for the kernel
    image where possible).
- **JIT compiler**: Target a 20% reduction in compile time and a
  10% reduction in native code size. Likely wins:
  - Memoize metadata lookups.
  - Cache resolved method references.
  - Avoid redundant type-system queries.
  - Improve the IL decoder's hot path (branch prediction, table
    dispatch).
- **GC**: Target a 50% reduction in pause time for gen-0 collections.
  Likely wins:
  - Implement a nursery (young-generation) area with bump allocation.
  - Use card-table write barriers to avoid full-heap scans.
  - Tune the LOH threshold.
  - Parallelize the mark phase on SMP systems.
- **Scheduler**: Target a 20% reduction in context-switch latency.
  Likely wins:
  - Per-CPU run queues with work stealing (if not already present).
  - Reduce lock contention in the scheduler.
  - Use `rdtsc` for fast path timing instead of the APIC timer.
- **Network stack**: Target a 2x improvement in loopback TCP
  throughput. Likely wins:
  - Zero-copy send/receive where the VirtIO-Net driver supports it.
  - Batch packet processing (NAPI-style).
  - Reduce per-packet allocations.
  - Offload checksums to the NIC if VirtIO-Net supports it.
- **TLS/SSH**: Target a 30% reduction in handshake latency. Likely wins:
  - Cache session keys (TLS session resumption, SSH rekey).
  - Optimize the big-integer arithmetic used by RSA and ECDH.
  - Use hardware AES-NI and SHA extensions if the CPU supports them
    (detect via CPUID; QEMU and VirtualBox can expose these).
- **File I/O**: Target a 2x improvement in sequential read/write
  throughput on EXT2. Likely wins:
  - Add a page cache for file data.
  - Batch directory-entry reads.
  - Use larger block sizes where the filesystem supports it.
- For each optimization, record before/after benchmark numbers in
  `docs/PHASE7-PERF-RESULTS.md`.

## Task 3 — Security hardening

Audit and harden the system against memory-safety, privilege-escalation,
and network-facing threats.

- **Memory safety**:
  - Enable **W^X** (write-xor-execute) for all user-mode pages. The
    JIT must allocate executable pages with the NX bit cleared and
    write-protect them after emitting code.
  - Enable **ASLR** for user-mode processes: randomize the base
    address of the executable, the stack, and the heap at process
    creation.
  - Enable **stack canaries** in AOT-compiled code (bflat supports
    `-fstack-protector-strong` via LLVM; verify it is enabled for
    the kernel and all AOT code).
  - Enable **guard pages** at the end of the stack and heap to catch
    overflow and underflow.
  - Audit the kernel's physical memory allocator for double-free and
    use-after-free bugs. Add poisoning and quarantine on free (debug
    builds only).
- **Privilege separation**:
  - Verify that Ring 3 user processes cannot read or write kernel
    memory. Add a test that attempts to access kernel addresses from
    user mode and verifies that a page fault occurs.
  - Verify that the syscall interface validates all user-supplied
    pointers (range check against the process's address space).
  - Verify that the syscall interface validates all user-supplied
    lengths and indices (no integer overflow, no negative lengths).
  - Add a **syscall filter** (seccomp-like) that allows a process to
    restrict its own syscall set. Configuration via a new syscall
    `sys_filter_install`.
- **Network-facing hardening**:
  - Add **rate limiting** to the SSH server (max authentication
    attempts per IP per minute; lockout after N failures).
  - Add **rate limiting** to the web server (max requests per IP per
    second; return 429 on excess).
  - Add **connection limits** per IP and globally.
  - Verify that TLS rejects weak cipher suites, expired certificates,
    and invalid hostnames.
  - Verify that SSH rejects weak key exchange, host key, cipher, and
    MAC algorithms by default (allow only the modern set).
  - Add **audit logging** for authentication events (SSH login
    success/failure, password changes, `authorized_keys` changes).
    Log to `/var/log/auth.log`.
- **Secure defaults**:
  - Disable password authentication for SSH by default; require
    public-key authentication. (Allow password auth via explicit
    config.)
  - Disable root SSH login by default; require `PermitRootLogin no`.
  - Disable the web server by default; require explicit autostart.
  - Disable the packet filter by default (allow all) and require
    explicit configuration to restrict.
- **Crypto audit**:
  - Verify all cryptographic implementations against NIST/RFC test
    vectors (AES-GCM, ChaCha20-Poly1305, SHA-2, SHA-3, HMAC,
    Curve25519, Ed25519, RSA-PSS, ECDSA).
  - Add constant-time comparisons for all secret-dependent branches
    (MAC verification, password hash verification).
  - Verify that the CSPRNG is properly seeded and cannot be predicted.
- Document the security model in `docs/PHASE7-SECURITY.md`, including
  the threat model, the mitigations implemented, and the known
  residual risks.

## Task 4 — Release packaging

Produce installable release artifacts for the three supported
hypervisors (VirtualBox, QEMU, raw disk).

- **Versioning**:
  - Adopt semantic versioning: `MAJOR.MINOR.PATCH`.
  - Embed the version string in the kernel banner, `uname -a`, and
    `/etc/neutrinoos-release`.
  - Add a `--version` flag to the shell and to all utilities.
- **VirtualBox OVA**:
  - Create a `.ova` appliance that includes:
    - The NeutrinoOS `.vdi` disk image.
    - A `.ovf` descriptor with UEFI firmware, 2 GB RAM, 1–4 CPUs,
      VirtIO-Net or E1000 NIC, and a serial port redirected to a
      host file.
  - Sign the `.ova` with a SHA-256 checksum published alongside the
    release.
- **QEMU qcow2**:
  - Create a `.qcow2` image suitable for `qemu-system-x86_64` with
    OVMF firmware. Include a `run-qemu.ps1` script for Windows 11
    that launches QEMU with the correct arguments.
- **Raw disk image**:
  - Create a `.img` image suitable for `dd` to a physical disk or
    USB drive. Include a `flash-usb.ps1` script for Windows 11 that
    uses `dd` for Windows or `Rufus` to write the image.
- **Reproducible builds**:
  - Pin all toolchain versions (bflat, .NET SDK, LLVM, make,
    Python) in a `toolchain.lock` file.
  - Verify that building from the same source produces a
    byte-identical image (modulo timestamps in the filesystem).
  - Provide a `make reproducible` target that builds twice and
    compares the two images.
- **Release manifest**:
  - Produce a `release.json` file listing:
    - Version, build date, git commit hash.
    - SHA-256 checksums of all artifacts.
    - Toolchain versions used.
    - Known limitations.
  - Sign `release.json` with a detached GPG signature (using a
    project key; document how to generate and publish the public
    key).
- **Windows 11 installer**:
  - Create `install-neutrinoos.ps1` (PowerShell 7) that:
    - Downloads the latest release artifact.
    - Verifies the SHA-256 checksum.
    - Installs the `.ova` into VirtualBox (via `VBoxManage import`).
    - Creates a desktop shortcut to launch the VM.
    - Configures the serial port to log to
      `%USERPROFILE%\NeutrinoOS\serial.log`.
  - Document the installer in `docs/PHASE7-INSTALL-WINDOWS.md`.
- Document the release process in `docs/PHASE7-RELEASE.md`.

## Task 5 — Release verification and acceptance

Verify the release artifacts against the Phase 1–6 acceptance criteria
on a fresh Windows 11 machine.

- **Fresh-machine verification**:
  - Use a clean Windows 11 VM (or a clean Windows 11 install) to run
    the installer.
  - Verify that the VM boots to a shell prompt on both serial and VGA
    consoles.
  - Verify that `uname -a` reports the correct version.
  - Verify that SSH login works (public-key only by default).
  - Verify that the web server serves an HTTPS page.
  - Verify that all Phase 1–6 acceptance criteria still pass.
- **Regression suite**:
  - Extend `tests/run-phase6-tests.ps1` to
    `tests/run-phase7-tests.ps1`, adding performance and security
    checks:
    - Boot time is within the Phase 7 target (30% faster than
      Phase 6).
    - TCP throughput is within the Phase 7 target (2x Phase 6
      loopback).
    - TLS handshake latency is within the Phase 7 target (30%
      faster than Phase 6).
    - ASLR is enabled (verify by running a program that prints the
      stack address twice; the addresses must differ).
    - W^X is enforced (verify that a JIT-emitted page is not
      writable).
    - Syscall filter works (verify that a filtered process cannot
      call a disallowed syscall).
    - SSH rate limiting works (verify that N failed logins trigger
      a lockout).
    - Audit log contains the expected authentication events.
- **Security audit report**:
  - Produce `docs/PHASE7-AUDIT.md` summarizing the security audit,
    the vulnerabilities found, the mitigations implemented, and the
    residual risks.
- Produce `docs/PHASE7-ACCEPTANCE.md` and `PHASE7-REPORT.md`.

## Task 6 — Documentation and release notes

- **Release notes**: Produce `RELEASE-NOTES-vX.Y.Z.md` summarizing:
  - What is new in this release.
  - Performance improvements (with before/after numbers).
  - Security fixes and mitigations.
  - Known limitations.
  - Upgrade instructions.
- **User manual**: Produce `docs/USER-MANUAL.md` covering:
  - Installation (VirtualBox, QEMU, raw disk).
  - First-boot configuration (root password, network, SSH keys).
  - Shell usage (commands, pipes, redirection, background jobs).
  - Writing and deploying .NET 10 console applications.
  - Writing and deploying .NET 10 web applications.
  - SSH server configuration.
  - Security best practices.
  - Troubleshooting (serial console access, network debugging,
    recovery).
- **Developer guide**: Produce `docs/DEVELOPER-GUIDE.md` covering:
  - Building NeutrinoOS from source (WSL2 + Windows 11).
  - The kernel architecture.
  - The Tier-0 JIT compiler.
  - The `korlib` BCL subset.
  - Writing drivers.
  - Writing shell utilities.
  - Contributing guidelines.
- **API reference**: Generate XML-doc-based API reference for
  `korlib` and the kernel public surface, published as
  `docs/api/`.

# CONSTRAINTS

- All code must be C# (plus the existing assembly intrinsics). Do NOT
  add C or C++ files to the kernel, bootloader, drivers, `korlib`,
  shell, utilities, SSH server, or web server.
- Do NOT introduce a graphical framebuffer, GUI, mouse support, or a
  window manager. This phase is strictly console-only.
- Do NOT depend on OpenSSL, libssh, libcurl, or any other native
  library. All cryptography, TLS, and SSH must remain in managed C#.
- Do NOT implement a graphical installer. The Windows 11 installer
  is a PowerShell script that uses `VBoxManage` and `dd`.
- Do NOT implement a package manager. Release artifacts are whole-
  system images, not packages.
- Do NOT implement multi-architecture support (ARM64, RISC-V). This
  phase is x86-64 only.
- Do NOT use the term "TTY" as a project name or suffix. It is fine
  to use the Unix term "tty" in device paths (`/dev/ttyS0`) and
  documentation.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to ProtonOS.
- Do NOT scope-creep into Phase 8+ (e.g., graphical installer,
  package manager, multi-architecture, external security audit).
  If a Phase 8+ concern arises, note it in the "Deferred to later
  phases" section.
- Every public type and method added to `korlib`, the kernel, the
  shell, or the utilities must have XML doc comments describing its
  Phase 7 semantics.
- All user-visible strings must say "NeutrinoOS".

# DELIVERABLES

1. Profiling infrastructure: kernel profiler, JIT statistics, GC
   statistics, network counters, boot timeline, and a benchmark
   suite of .NET 10 applications.
2. Performance optimizations in boot, JIT, GC, scheduler, network
   stack, TLS/SSH, and file I/O, with before/after benchmark
   numbers in `docs/PHASE7-PERF-RESULTS.md`.
3. Security hardening: W^X, ASLR, stack canaries, guard pages,
   syscall filtering, SSH/web rate limiting, connection limits,
   audit logging, secure defaults, and a crypto audit against
   NIST/RFC test vectors.
4. Release packaging: versioned VirtualBox `.ova`, QEMU `.qcow2`,
   raw `.img`, reproducible builds, `release.json` manifest with
   GPG signature, and a Windows 11 PowerShell installer.
5. `tests/run-phase7-tests.ps1`, `tests/benchmarks/`, and
   `install-neutrinoos.ps1`.
6. `docs/PHASE7-PROFILING.md`, `docs/PHASE7-PERF-RESULTS.md`,
   `docs/PHASE7-SECURITY.md`, `docs/PHASE7-AUDIT.md`,
   `docs/PHASE7-RELEASE.md`, `docs/PHASE7-INSTALL-WINDOWS.md`,
   `docs/PHASE7-ACCEPTANCE.md`, `PHASE7-REPORT.md`,
   `RELEASE-NOTES-vX.Y.Z.md`, `docs/USER-MANUAL.md`,
   `docs/DEVELOPER-GUIDE.md`, `docs/api/`.

# ACCEPTANCE CRITERIA

Phase 7 is complete when ALL of the following are true:

- [ ] `make image` and `make run-qemu-vga` still boot to a NeutrinoOS
      banner on both consoles, with no regressions from Phase 6.
- [ ] Boot time (from QEMU start to shell prompt) is at least 30%
      faster than Phase 6, verified by `boottime`.
- [ ] Loopback TCP throughput is at least 2x Phase 6, verified by the
      benchmark suite.
- [ ] TLS handshake latency is at least 30% faster than Phase 6,
      verified by the benchmark suite.
- [ ] GC gen-0 pause time is at least 50% faster than Phase 6,
      verified by `gcstats`.
- [ ] JIT compile time is at least 20% faster than Phase 6, verified
      by `jitstats`.
- [ ] ASLR is enabled: a program that prints the stack address twice
      (across two runs) reports different addresses.
- [ ] W^X is enforced: a JIT-emitted page is executable but not
      writable after emission.
- [ ] The syscall filter works: a filtered process cannot call a
      disallowed syscall.
- [ ] SSH rate limiting works: N failed logins from the same IP
      trigger a lockout.
- [ ] Web server rate limiting works: excess requests return 429.
- [ ] Password authentication is disabled for SSH by default;
      public-key authentication works.
- [ ] Root SSH login is disabled by default.
- [ ] Audit logging records SSH login success/failure events in
      `/var/log/auth.log`.
- [ ] All cryptographic implementations pass NIST/RFC test vectors.
- [ ] The VirtualBox `.ova` imports into VirtualBox 7.x on a fresh
      Windows 11 machine and boots to a shell prompt.
- [ ] The QEMU `.qcow2` boots under OVMF on a fresh Windows 11
      machine.
- [ ] The raw `.img` boots when flashed to a USB drive and booted on
      a UEFI x86-64 machine.
- [ ] `release.json` is present, contains SHA-256 checksums for all
      artifacts, and has a valid GPG signature.
- [ ] `make reproducible` produces byte-identical images across two
      builds (modulo filesystem timestamps).
- [ ] `install-neutrinoos.ps1` installs the `.ova` into VirtualBox on
      a fresh Windows 11 machine and creates a working desktop
      shortcut.
- [ ] `docs/USER-MANUAL.md` allows a new user to install, configure,
      and use NeutrinoOS without prior OS development knowledge.
- [ ] `docs/DEVELOPER-GUIDE.md` allows a new developer to build
      NeutrinoOS from source on Windows 11 + WSL2.
- [ ] No C or C++ files exist in the kernel, bootloader, driver,
      `korlib`, shell, utility, SSH server, or web server
      directories.
- [ ] `docs/BUILD-WINDOWS.md` (from Phase 1) and
      `docs/PHASE2-ACCEPTANCE.md` through
      `docs/PHASE6-ACCEPTANCE.md` still work, and
      `docs/PHASE7-ACCEPTANCE.md` provides step-by-step verification
      for every checklist item above from a fresh Windows 11 machine.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps mapped to the six
   tasks above.
2. **Repository layout** — the target directory tree after Phase 7,
   highlighting new and modified files.
3. **Code changes** — for each file to be created, modified, or
   deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created files) OR a unified
     diff (for modifications) OR a precise description (for
     deletions).
   - For large files (e.g., the GC nursery, the TLS optimization,
     the release scripts), provide the complete source; do not
     abbreviate with "..." unless the omitted region is boilerplate
     that is explicitly described.
4. **Performance results table** — a table listing each optimization,
   the before/after benchmark numbers, and the percentage
   improvement.
5. **Security mitigation table** — a table listing each mitigation,
   the threat it addresses, and how it is verified.
6. **Release artifact table** — a table listing each artifact, its
   format, its size, and the command to install or run it.
7. **Build and test commands** — exact WSL2 bash commands and
   PowerShell commands for Windows 11 to build, benchmark, verify,
   and release Phase 7.
8. **Acceptance checklist** — reproduce the checklist above, with a
   one-line note for each item explaining how it is satisfied.
9. **Deferred to later phases** — anything that came up that belongs
   to Phase 8+ (graphical installer, package manager,
   multi-architecture, external security audit, kernel module
   signing, secure boot).
10. **Open questions / assumptions** — anything ambiguous about the
    Phase 6 output, the existing TCP/IP stack, the existing `korlib`
    structure, the ProtonOS conventions, or the .NET 10 performance
    characteristics that you assumed, and how the user can verify
    or correct them.

If any part of the Phase 6 output is unclear, or if the existing
TCP/IP stack or `korlib` layout does not match your assumptions,
state your assumptions explicitly and proceed with a reasonable
layout consistent with a bflat-based managed kernel, noting where
the user must adjust paths.

Do not skip ahead to Phase 8+. Scope discipline is mandatory:
Phase 7 only.