# ROLE

You are a senior systems engineer specializing in operating system
ecosystem design, package manager architecture, device driver
frameworks, cross-platform kernel porting, and developer tooling for
managed runtimes. You are assisting in Phase 8 of a custom operating
system project.

# PROJECT CONTEXT

Project name: NeutrinoOS
Base project: ProtonOS (a managed OS written entirely in C# using
  bflat's zero-library mode, with a Tier-0 JIT compiler).

Phase 1 status: COMPLETE. Graphics/framebuffer/GOP removed. Serial
  console at COM1 (0x3F8, 115200 8N1).

Phase 2 status: COMPLETE. UART 16550 driver (`/dev/ttyS0`) with
  interrupt-driven RX/TX. Line discipline with canonical/raw modes,
  editing, Ctrl+C/D/U, 32-entry history. Console Abstraction Layer
  (CAL) with `IConsoleDevice` and `ConsoleMultiplexer`. `korlib`
  implements `System.Console`, `System.IO.TextWriter`/`TextReader`,
  `System.ConsoleColor`, `System.ConsoleKey`,
  `System.Text.Encoding.UTF8`, `System.Environment`.

Phase 3 status: COMPLETE. VGA text-mode driver (`/dev/vga0`,
  80x25/80x50, ANSI parser, CP437). PS/2 keyboard driver (IRQ1,
  scancode set 1, key repeat, modifier tracking). CAL routes output
  to both consoles and switches active input automatically.

Phase 4 status: COMPLETE. Tier-0 JIT validates .NET 10 assemblies
  (C# 14). `korlib` expanded with `System.IO`,
  `System.Collections.Generic`, `System.Linq`, `System.Threading`,
  `System.Threading.Tasks` (synchronous minimal), `System.Text`,
  `System`, `System.Net`, `System.Globalization`,
  `System.Diagnostics`. Cross-assembly loading with
  `AssemblyLoadContext`. Standard .NET 10 console applications run
  on NeutrinoOS.

Phase 5 status: COMPLETE. Production shell with tokenizer, parser,
  pipes (`|`), redirection (`>`, `>>`, `<`, `2>`, `2>>`),
  background execution (`&`), sequential (`;`) and conditional
  (`&&`, `||`) execution. Built-in commands and external utilities
  (`.dll`, .NET 10) including `ls`, `cat`, `echo`, `mkdir`, `rm`,
  `cp`, `mv`, `wc`, `grep`, `ps`, `kill`, `sleep`, `df`, `mount`,
  `umount`, `uname`, `date`, `uptime`, `free`, `env`, `ifconfig`,
  `dhcp`, `ping`, `dns`, `netstat`, `wget`, `curl`, `ssh` (client),
  `gc`, `history`, `export`, `unset`. Shell initialization with
  `/etc/profile`, `PS1` customization, persistent history, basic
  tab completion.

Phase 6 status: COMPLETE. Hardened TCP/IP stack with POSIX-like
  socket API. Managed C# cryptographic primitives (SHA family,
  HMAC, AES-GCM, ChaCha20-Poly1305, ECDH, Curve25519, RSA, ECDSA,
  Ed25519, CSPRNG). TLS 1.2/1.3. SSH-2.0 server (sshd) with
  password and publickey authentication, PTY-backed shell
  sessions, and `exec`/`subsystem` requests. Minimal user
  database. .NET 10 web hosting via ported Kestrel (or fallback
  HTTP/1.1 server) with HTTP and HTTPS support. `/dev/random`.
  Minimal packet filter.

Phase 7 status: COMPLETE. Profiling infrastructure (kernel
  profiler, JIT statistics, GC statistics, network counters, boot
  timeline, benchmark suite). Performance optimizations in boot,
  JIT, GC, scheduler, network stack, TLS/SSH, and file I/O with
  measured improvements. Security hardening: W^X, ASLR, stack
  canaries, guard pages, syscall filtering, SSH/web rate
  limiting, connection limits, audit logging, secure defaults,
  crypto audit against NIST/RFC test vectors. Release packaging:
  VirtualBox `.ova`, QEMU `.qcow2`, raw `.img`, reproducible
  builds, `release.json` manifest with GPG signature, Windows 11
  PowerShell installer. Boot time 30% faster, loopback TCP
  throughput 2x, TLS handshake 30% faster, GC gen-0 pause 50%
  faster, JIT compile time 20% faster.

Target of the overall project: A console-only, headless managed
  OS that runs .NET 10 console applications and utilities on bare
  metal, with TCP/IP networking, SSH, curl, and the ability to
  host .NET web apps. No GUI, no graphical framebuffer, no
  window manager.

# ENVIRONMENT

- Host OS: Windows 11 (x64)
- IDE: Visual Studio Code (latest stable) with the Remote - WSL
  extension
- Shell: PowerShell 7 on the host; bash inside WSL2 Ubuntu 24.04
- Toolchain (already installed in WSL2 from Phase 1):
  - .NET SDK 10.0 (with C# 14)
  - bflat (configured to target .NET 10)
  - clang / ld.lld (LLVM 17+)
  - GNU make
  - Python 3.11+
  - qemu-system-x86_64 with OVMF firmware
  - qemu-system-aarch64 with AAVMF firmware (for ARM64 testing;
    install if not present)
  - git
- Phase 7 boot verification used:
  `make run-qemu-vga` (boots to NeutrinoOS shell on serial and
  VGA). `tests/run-phase7-tests.ps1` verifies performance,
  security, and release artifacts.
- Windows 11 host has VirtualBox 7.x installed for manual
  verification.

# PHASE 8 GOAL — "ECOSYSTEM, DEVELOPER PLATFORM, AND MULTI-ARCHITECTURE"

Transform NeutrinoOS from a standalone operating system into a
platform with a thriving ecosystem. Phase 8 delivers four
integrated capabilities:

1. **Package manager**: A native NeutrinoOS package manager for
   distributing .NET applications, utilities, drivers, and system
   components. Packages are signed, dependency-resolved, and
   installable/upgradable/removable via a single command.

2. **Driver framework**: A structured device driver model with a
   driver registry, versioned driver packages, hot-plug support,
   and a stable driver ABI so third parties can write drivers
   without rebuilding the kernel.

3. **Multi-architecture support**: An ARM64 (AArch64) port of the
   NeutrinoOS kernel, bootloader, and core drivers, enabling
   NeutrinoOS to run on ARM64 servers, development boards, and
   ARM64 virtual machines.

4. **Developer platform**: SDK, templates, documentation, and
   tooling that enable third-party developers to write, test,
   package, and publish NeutrinoOS applications, utilities, and
   drivers.

Phase 8 is complete when a third-party developer can install the
NeutrinoOS SDK on Windows 11, write a .NET 10 console application
or driver, package it as a `.nupkg`-style NeutrinoOS package,
publish it to a local repository, install it on a running
NeutrinoOS system via `npkg install`, and run it — all on both
x86-64 and ARM64.

Phase 8 does NOT include: a graphical package manager UI, a
public package repository (a local file-based repository is
sufficient), a full CI/CD pipeline, or a community governance
model. It is strictly about the technical platform that enables
an ecosystem.

# DETAILED TASKS

## Task 1 — NeutrinoOS Package Manager (npkg)

Design and implement a native package manager for NeutrinoOS,
written in C# and running as a .NET 10 application on the
Tier-0 JIT.

- **Package format**: Define a `.npkg` package format (a ZIP
  archive with a defined internal structure). Contents:
  - `manifest.json`: Package metadata (name, version, author,
    description, license, dependencies, architecture,
    entry points, install paths, pre/post-install scripts).
  - `payload/`: The package files (`.dll` assemblies, data
    files, configuration files).
  - `signature.sig`: A detached signature over the manifest and
    payload, using Ed25519 (implemented in Phase 6).
  - `checksums.sha256`: SHA-256 checksums of every file in the
    payload.
- **Package manifest schema**: Define the `manifest.json` schema
  (JSON Schema or a documented C# class). Fields:
  - `name` (e.g., `neutrinoos.utils.core`)
  - `version` (semver)
  - `architecture` (`x86-64`, `arm64`, `any`)
  - `dependencies` (map of package name to version constraint)
  - `provides` (capabilities this package provides, e.g.,
    `shell-utility`, `driver`)
  - `entryPoints` (executable names and their assembly paths)
  - `installPath` (default `/bin`, `/apps`, or `/drivers`)
  - `scripts` (pre-install, post-install, pre-remove,
    post-remove)
  - `signature` (Ed25519 public key fingerprint of the author)
- **Repository format**: Define a repository index format
  (`repository.json`) listing all packages, their versions,
  checksums, and signatures. Support file-based repositories
  (local directory or HTTP URL). The repository index is itself
  signed.
- **Dependency resolver**: Implement a resolver that:
  - Reads the installed package database
    (`/var/lib/npkg/installed.json`).
  - Reads the repository index.
  - Resolves dependencies transitively (topological sort).
  - Detects and reports version conflicts.
  - Supports version constraints: `=1.2.3`, `>=1.0.0`,
    `>=1.0.0 <2.0.0`, `~1.2.3` (compatible), `^1.2.3`
    (semver-compatible).
- **Install / remove / upgrade**:
  - `npkg install <package>`: Resolve dependencies, download
    (or copy from local repo), verify signature and checksums,
    extract to a staging area, run pre-install scripts, move
    files into place, run post-install scripts, update the
    installed database.
  - `npkg remove <package>`: Run pre-remove scripts, remove
    files, run post-remove scripts, update the installed
    database. Refuse to remove a package that other installed
    packages depend on (unless `--force`).
  - `npkg upgrade [<package>]`: Upgrade one or all packages to
    the latest compatible version.
  - `npkg list`: List installed packages with versions.
  - `npkg search <query>`: Search the repository index.
  - `npkg info <package>`: Show package metadata.
  - `npkg repo add <name> <url>`: Add a repository.
  - `npkg repo list`: List configured repositories.
  - `npkg repo remove <name>`: Remove a repository.
- **Atomicity and rollback**: Package installation must be
  atomic. If any step fails, roll back to the previous state.
  Use a transaction log at `/var/lib/npkg/journal.log`.
- **Signing and verification**:
  - `npkg sign <package.npkg> <private-key>`: Sign a package.
  - `npkg verify <package.npkg>`: Verify a package signature
    and checksums.
  - The repository index must be signed. `npkg repo add` fetches
    and verifies the repository's public key.
  - Reject packages from untrusted signers by default. Allow
    trusted signers via `/etc/npkg/trusted-keys/`.
- **Driver packages**: A driver package is a special package
  with `provides: ["driver"]` and a `driver` section in its
  manifest describing:
  - The device class it supports (e.g., `net`, `block`,
    `input`).
  - The vendor and device IDs it matches (for PCI/PCIe
    devices).
  - The driver entry point (a class implementing
    `IDriver`).
- **Application packages**: An application package has
  `provides: ["application"]` and one or more `entryPoints`.
  Installing it places the `.dll` in `/apps/<name>/` and
  creates a shell wrapper in `/bin/<name>` that invokes
  `run /apps/<name>/<entry>.dll`.
- **Utility packages**: A utility package has
  `provides: ["utility"]` and a single entry point. Installing
  it places the `.dll` in `/bin/` and creates a shell wrapper.
- **Implementation guidance**: Reference the Cosmos package
  manager (`cosmospkg/cosmos`) for design inspiration. Cosmos
  is a Rust-based, static, musl-first package manager designed
  for bare metal and broken installs, making it directly
  relevant. Also reference `macaroni-os/anise` for
  container-based package building concepts and `RakuOS`'s
  `Rum` package manager for split-mode transaction design.
- **Tests**: `tests/npkg/` with test packages (a utility, an
  application, a driver) and a test script that installs,
  upgrades, and removes them, verifying the installed database,
  file placement, and signature verification.
- Document the package manager in `docs/PHASE8-NPKG.md`.

## Task 2 — Driver framework and device model

Implement a structured device driver model with a stable ABI.

- **Device model**:
  - `IDevice`: Represents a device instance (vendor ID,
    device ID, class, resources: I/O ports, MMIO regions,
    IRQ lines, DMA channels).
  - `IDriver`: Represents a driver (matches devices, probes,
    initializes, starts, stops, removes).
  - `IDriverHost`: A process that hosts one or more drivers.
    Drivers run in user-mode driver hosts, not in the kernel,
    for safety and upgradability. This follows the Fuchsia
    driver framework model, where drivers are loaded into
    Driver Host processes and managed by a Driver Manager
    process.
  - `IDeviceTree`: A hierarchical tree of devices, populated
    by bus enumerators (PCI, USB, VirtIO).
- **Driver registry**:
  - A kernel-maintained registry of devices and drivers.
  - On boot, bus enumerators (PCI, VirtIO, USB) populate the
    device tree. The driver manager matches devices to
    drivers based on vendor/device IDs and driver manifest
    metadata.
  - Drivers are loaded from `/drivers/` (built-in) or from
    installed driver packages (in `/var/lib/npkg/drivers/`).
  - Driver versioning: each driver declares a version and the
    kernel ABI version it targets. The kernel rejects
    incompatible drivers.
- **Driver ABI**:
  - Define a stable binary interface between the kernel and
    driver hosts. Since both are managed C#, the ABI is a
    set of C# interfaces in a shared assembly
    (`NeutrinoOS.Driver.Abstractions.dll`) versioned with
    semantic versioning.
  - The kernel exposes a `IDriverServices` interface to
    driver hosts, providing:
    - `MapMmio(physicalAddress, size)` -> virtual address.
    - `AllocateDma(size, alignment)` -> physical address.
    - `RegisterInterrupt(irq, handler)`.
    - `CreateDeviceNode(name, major, minor)`.
    - `Log(level, message)`.
  - Driver hosts expose `IDriverHostServices` to the kernel,
    providing:
    - `OnDeviceAdded(device)`.
    - `OnDeviceRemoved(device)`.
    - `OnInterrupt(irq)`.
- **Bus enumerators**:
  - **PCI/PCIe**: Enumerate the PCI bus (configuration space
    at 0xCF8/0xCFC for legacy, ECAM for modern). Populate
    the device tree with vendor ID, device ID, class,
    BARs (MMIO and I/O), and interrupt line. This is a
    prerequisite for VirtIO and E1000 drivers.
  - **VirtIO**: Enumerate VirtIO devices on the PCI bus
    (vendor 0x1AF4). Populate the device tree with the
    VirtIO device type (net, block, console, etc.).
  - **USB**: Out of scope for Phase 8 (defer to Phase 9+).
    Document the limitation.
- **Hot-plug support**:
  - On PCIe, implement hot-plug detection via the PCIe
    capability structure and the `Attention Button`
    and `Power Indicator` control.
  - When a device is added or removed, the driver manager
    loads or unloads the matching driver.
- **Driver development**:
  - Provide a `NeutrinoOS.Driver.Sdk` NuGet package with the
    `IDriver` interfaces, base classes, and helper methods.
  - Provide a `templates/NeutrinoDriver` project template
    for writing a new driver.
  - Provide a `scripts/build-driver.ps1` PowerShell script
    for Windows 11 that compiles a driver project, packages
    it as a `.npkg` driver package, and signs it.
- **Port existing drivers**:
  - Port the UART 16550, VGA text, PS/2 keyboard, VirtIO-Net,
    E1000, VirtIO-BLK, and AHCI drivers from the kernel into
    the new driver framework.
  - Verify that all Phase 1–7 functionality still works with
    drivers running in user-mode driver hosts.
- Document the driver framework in `docs/PHASE8-DRIVER.md`.

## Task 3 — ARM64 (AArch64) port

Port NeutrinoOS to ARM64. This is a substantial undertaking;
scope it carefully.

- **Toolchain**: bflat supports ARM64 targets
  (`aarch64-unknown-none-elf` for bare metal and
  `aarch64-unknown-uefi` for UEFI). Verify that bflat can
  compile the NeutrinoOS kernel for ARM64. If bflat's ARM64
  support is incomplete, document the limitations and implement
  the missing pieces.
- **Bootloader**: Port the UEFI bootloader to ARM64. The
  ARM64 UEFI boot flow differs from x86-64:
  - ARM64 uses `PE32+` executables for UEFI applications, same
    as x86-64.
  - ARM64 has a different set of UEFI protocols and a
    different memory map format.
  - ARM64 uses `AArch64` exception levels (EL0–EL3) instead
    of x86 ring levels. The kernel runs at EL1; user
    processes run at EL0.
  - ARM64 uses `VBAR_EL1` for the exception vector base
    instead of the x86 IDT.
  - ARM64 uses `TTBR0_EL1` and `TTBR1_EL1` for page tables
    instead of x86 CR3.
- **Kernel**:
  - **Exception handling**: Implement ARM64 exception
    vectors (synchronous, IRQ, FIQ, SError) at `VBAR_EL1`.
  - **Page tables**: Implement ARM64 4-level or 3-level
    paging. Use 4 KB granule (or 16 KB for larger systems;
    document the choice). Implement higher-half kernel
    mapping via `TTBR1_EL1`.
  - **Interrupts**: Implement the ARM64 Generic Interrupt
    Controller (GIC) v2 or v3. The GICv3 is required for
    modern ARM64 servers; GICv2 is sufficient for QEMU's
    `virt` machine.
  - **Timer**: Implement the ARM64 generic timer
    (`CNTPCT_EL0` for physical counter, `CNTP_TVAL_EL0` for
    timer compare).
  - **Scheduler**: Port the preemptive scheduler to ARM64.
    Use `DAIF` (Debug, SError, IRQ, FIQ) mask bits for
    interrupt enable/disable.
  - **Syscalls**: Port the Linux-compatible syscall ABI to
    ARM64. ARM64 Linux uses `svc #0` with the syscall
    number in `x8` and arguments in `x0`–`x5`. Return
    value in `x0`.
  - **SMP boot**: Implement ARM64 SMP boot via PSCI
    (`CPU_ON`, `CPU_OFF`). The bootloader reads the PSCI
    conduit (HVC or SMC) from the UEFI device tree.
- **Drivers**:
  - Port the UART driver. ARM64 QEMU `virt` uses the
    PL011 UART at `0x09000000` instead of the 16550 at
    0x3F8. Implement a PL011 driver and register it as
    `/dev/ttyS0` on ARM64.
  - Port the GIC driver (v2 for QEMU `virt`).
  - Port the VirtIO drivers (net, block, console). VirtIO
    on ARM64 uses the same PCI or MMIO transport as
    x86-64; the MMIO transport is more common on ARM64
    embedded systems.
  - Port the PCI/PCIe enumerator for ARM64 (ECAM base
    address is read from the ACPI MCFG table or the
    device tree).
- **QEMU testing**:
  - Add a `make run-qemu-arm64` target that launches
    `qemu-system-aarch64` with:
    - `-machine virt`
    - `-cpu cortex-a72` (or `max` for all features)
    - `-m 2G`
    - `-bios /usr/share/AAVMF/AAVMF_CODE.fd`
    - `-drive if=pflash,format=raw,readonly=on,file=AAVMF_CODE.fd`
    - `-drive if=pflash,format=raw,file=AAVMF_VARS.fd`
    - `-drive file=neutrinoos-arm64.img,format=raw,if=virtio`
    - `-serial stdio`
    - `-display none`
  - Verify that NeutrinoOS boots to a shell prompt on
    ARM64 QEMU with the PL011 UART as the serial console.
- **Cross-compilation**:
  - Add a `make kernel ARCH=arm64` target that invokes
    bflat with the ARM64 target.
  - Add a `make image ARCH=arm64` target that produces an
    ARM64 disk image.
  - Ensure the build system supports building both
    architectures from the same source tree without
    conflicts (use separate `build/x86-64/` and
    `build/arm64/` directories).
- **VirtualBox and ARM64**: VirtualBox 7.x on Windows 11
  x64 does not support ARM64 guests. Document that ARM64
  testing is done via QEMU. On ARM64 hosts (Windows 11 on
  ARM, Apple Silicon via UTM), VirtualBox or QEMU can be
  used; document the options.
- Document the ARM64 port in `docs/PHASE8-ARM64.md`,
  including the architecture-specific code, the boot flow,
  the driver differences, and the QEMU testing setup.

## Task 4 — Developer SDK and tooling

Create an SDK that enables third-party developers to write,
test, package, and publish NeutrinoOS applications, utilities,
and drivers from Windows 11.

- **SDK contents**:
  - `NeutrinoOS.Sdk` NuGet package (or a direct download
    archive) containing:
    - `NeutrinoOS.Runtime.Abstractions.dll`: The public
      `korlib` surface for applications.
    - `NeutrinoOS.Driver.Abstractions.dll`: The driver
      interfaces.
    - `NeutrinoOS.Packaging.dll`: APIs for building
      `.npkg` packages programmatically.
    - Project templates (`dotnet new` templates):
      - `neutrino-console`: A .NET 10 console application.
      - `neutrino-utility`: A shell utility.
      - `neutrino-driver`: A device driver.
      - `neutrino-webapp`: An ASP.NET Core web application.
      - `neutrino-library`: A class library.
    - MSBuild targets that automatically package the
      application as a `.npkg` on `dotnet build -c Release`.
    - `npkg` CLI tool (the package manager from Task 1)
      for Windows 11, so developers can sign and publish
      packages from the host.
  - Documentation:
    - `docs/SDK-GETTING-STARTED.md`: Install the SDK,
      create a project, build, package, publish.
    - `docs/SDK-API-REFERENCE.md`: API reference for the
      `korlib` subset, the driver interfaces, and the
      packaging APIs (generated from XML doc comments).
    - `docs/SDK-PACKAGING.md`: How to create, sign, and
      publish a `.npkg` package.
    - `docs/SDK-TESTING.md`: How to test a NeutrinoOS
      application in QEMU/VirtualBox from Windows 11.
- **Developer workflow**:
  - `dotnet new neutrino-console -n MyApp`
  - `cd MyApp`
  - `dotnet build -c Release`
  - This produces `MyApp.npkg` in `bin/Release/net10.0/`.
  - `npkg sign MyApp.npkg ~/.neutrinoos/private.key`
  - `npkg publish MyApp.npkg --repo http://localhost:8080`
  - On NeutrinoOS: `npkg repo add local
    http://host-ip:8080` then `npkg install MyApp`.
- **Local repository server**:
  - Provide a simple repository server (`npkg-repo-server`)
    that can be run on Windows 11 (as a .NET 10 console
    application) to serve a local repository over HTTP.
  - The server reads a directory of `.npkg` files, generates
    the `repository.json` index, signs it with a configured
    private key, and serves it over HTTP.
  - Add a `scripts/start-repo-server.ps1` PowerShell script
    for Windows 11.
- **CI/CD integration**:
  - Provide a GitHub Actions workflow template that builds,
    tests, packages, and publishes a NeutrinoOS application
    on every push.
  - Provide a `.gitlab-ci.yml` template for GitLab CI.
  - Document the CI/CD integration in
    `docs/SDK-CICD.md`.
- **Sample projects**:
  - `samples/hello-console/`: A minimal console app.
  - `samples/file-utility/`: A shell utility that reads
    and writes files.
  - `samples/hello-driver/`: A minimal driver (e.g., a
    virtual LED device).
  - `samples/hello-webapp/`: A minimal ASP.NET Core web app.
  - `samples/hello-library/`: A class library used by
    `hello-console`.
- Document the SDK in `docs/PHASE8-SDK.md`.

## Task 5 — Ecosystem infrastructure

Provide the infrastructure that supports the ecosystem.

- **Package signing keys**:
  - Generate a project signing key for the NeutrinoOS
    project itself (used to sign the official repository
    index and official packages).
  - Document how third-party developers generate their own
    signing keys and publish their public keys.
  - Provide a `npkg keygen` command that generates an
    Ed25519 key pair and stores the private key in
    `~/.neutrinoos/private.key` and the public key in
    `~/.neutrinoos/public.key`.
- **Repository mirroring**:
  - Support multiple repositories with priority ordering.
  - Support HTTP and HTTPS repositories.
  - Support local file repositories (`file:///path/to/repo`).
- **Package templates**:
  - Provide a `templates/package-templates/` directory with
    ready-to-use `.npkg` templates for common package types
    (utility, application, driver, library, web app).
- **Community documentation**:
  - `docs/COMMUNITY-GUIDE.md`: How to contribute to the
    NeutrinoOS project, report bugs, request features, and
    submit packages to the community repository.
  - `docs/PACKAGE-GUIDELINES.md`: Naming conventions,
    versioning rules, licensing requirements, and security
    requirements for packages.
  - `CODE_OF_CONDUCT.md`: A standard code of conduct.
  - `CONTRIBUTING.md`: Contribution guidelines.
- **Website (optional)**:
  - Provide a minimal static website
    (`website/` directory) that lists available packages,
    links to documentation, and provides a getting-started
    guide. The website can be hosted anywhere (GitHub
    Pages, Netlify, etc.). This is optional and may be
    deferred; document if deferred.

## Task 6 — Testing and documentation

- **Test suite**:
  - `tests/run-phase8-tests.ps1` (PowerShell for Windows 11)
    that:
    - Boots NeutrinoOS in QEMU (x86-64 and ARM64).
    - Verifies that `npkg` can install, upgrade, and remove
      test packages.
    - Verifies that the driver framework loads the
      ported drivers correctly.
    - Verifies that the ARM64 build boots to a shell prompt.
    - Verifies that the SDK templates produce working
      packages.
    - Verifies that the local repository server serves
      packages correctly.
    - Verifies that package signing and verification work.
    - Verifies that hot-plug works (add/remove a VirtIO
      device in QEMU and verify the driver loads/unloads).
- **Test packages**:
  - `tests/npkg/hello-utility/`: A utility package.
  - `tests/npkg/hello-app/`: An application package.
  - `tests/npkg/hello-driver/`: A driver package.
  - `tests/npkg/dependency-chain/`: Three packages with a
    dependency chain (A depends on B, B depends on C).
  - `tests/npkg/conflict/`: Two packages that conflict.
- **Benchmarks**:
  - Measure `npkg install` time for a package with 10
    dependencies.
  - Measure driver load time for a hot-plugged VirtIO device.
  - Measure ARM64 boot time (compare to x86-64).
- **Documentation**:
  - `docs/PHASE8-NPKG.md` — package manager design,
    package format, repository format, dependency
    resolution, signing.
  - `docs/PHASE8-DRIVER.md` — device model, driver
    framework, driver ABI, bus enumerators, hot-plug,
    driver development.
  - `docs/PHASE8-ARM64.md` — ARM64 port, boot flow,
    drivers, QEMU testing, cross-compilation.
  - `docs/PHASE8-SDK.md` — SDK contents, developer
    workflow, templates, local repository server,
    CI/CD integration.
  - `docs/PHASE8-ACCEPTANCE.md` — step-by-step
    verification for every acceptance criterion below
    from a fresh Windows 11 machine.
  - `PHASE8-REPORT.md` — summary of changes, blockers,
    deviations.

# CONSTRAINTS

- All code must be C# (plus the existing assembly
  intrinsics). Do NOT add C or C++ files to the kernel,
  bootloader, drivers, `korlib`, shell, utilities, SSH
  server, web server, package manager, or SDK.
- Do NOT introduce a graphical framebuffer, GUI, mouse
  support, or a window manager. This phase is strictly
  console-only.
- Do NOT depend on OpenSSL, libssh, libcurl, or any other
  native library. All cryptography, TLS, SSH, and
  signature verification must remain in managed C#.
- Do NOT implement a graphical package manager UI.
- Do NOT implement a public package repository. A local
  file-based or HTTP-based repository is sufficient.
- Do NOT implement a full CI/CD pipeline. Provide
  templates for GitHub Actions and GitLab CI, but do not
  set up a running pipeline.
- Do NOT implement USB support. Defer to Phase 9+.
- Do NOT implement a full community governance model.
  Provide documentation, not governance.
- Do NOT use the term "TTY" as a project name or suffix.
  It is fine to use the Unix term "tty" in device paths
  (`/dev/ttyS0`) and documentation.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to
  ProtonOS.
- Do NOT scope-creep into Phase 9+ (graphical package
  manager, public repository, USB support, community
  governance, full CI/CD pipeline).
- Every public type and method added to `korlib`, the
  kernel, the package manager, the driver framework, or
  the SDK must have XML doc comments describing its
  Phase 8 semantics.
- All user-visible strings must say "NeutrinoOS".

# DELIVERABLES

1. A native package manager (`npkg`) with `.npkg` package
   format, repository index format, dependency resolver,
   install/remove/upgrade/list/search/info/repo commands,
   atomic transactions, signing, and verification.
2. A device driver framework with `IDevice`, `IDriver`,
   `IDriverHost`, `IDeviceTree`, a driver registry, a
   stable driver ABI, PCI/PCIe and VirtIO bus enumerators,
   hot-plug support, and ported drivers (UART, VGA text,
   PS/2 keyboard, VirtIO-Net, E1000, VirtIO-BLK, AHCI).
3. An ARM64 (AArch64) port of the kernel, bootloader, and
   core drivers, booting under QEMU `virt` with AAVMF
   firmware and the PL011 UART as the serial console.
4. A developer SDK with `NeutrinoOS.Sdk` package, project
   templates (`neutrino-console`, `neutrino-utility`,
   `neutrino-driver`, `neutrino-webapp`,
   `neutrino-library`), MSBuild packaging targets,
   `npkg` CLI for Windows 11, and a local repository
   server.
5. Ecosystem infrastructure: package signing keys,
   repository mirroring, package templates, community
   documentation (`COMMUNITY-GUIDE.md`,
   `PACKAGE-GUIDELINES.md`, `CODE_OF_CONDUCT.md`,
   `CONTRIBUTING.md`).
6. Test suite: `tests/run-phase8-tests.ps1`,
   `tests/npkg/`, benchmarks.
7. Documentation: `docs/PHASE8-NPKG.md`,
   `docs/PHASE8-DRIVER.md`, `docs/PHASE8-ARM64.md`,
   `docs/PHASE8-SDK.md`, `docs/PHASE8-ACCEPTANCE.md`,
   `PHASE8-REPORT.md`.

# ACCEPTANCE CRITERIA

Phase 8 is complete when ALL of the following are true:

- [ ] `make image` and `make run-qemu-vga` still boot to a
      NeutrinoOS banner on both consoles, with no
      regressions from Phase 7.
- [ ] `npkg install <package>` installs a package and its
      dependencies, verifies the signature, and updates
      the installed database.
- [ ] `npkg remove <package>` removes a package and
      refuses to remove a package that others depend on
      (unless `--force`).
- [ ] `npkg upgrade` upgrades a package to the latest
      compatible version.
- [ ] `npkg list`, `npkg search`, `npkg info`, `npkg repo
      add/list/remove` all work as documented.
- [ ] A package with a tampered payload is rejected by
      signature verification.
- [ ] The driver framework loads all ported drivers
      (UART, VGA text, PS/2 keyboard, VirtIO-Net, E1000,
      VirtIO-BLK, AHCI) in user-mode driver hosts.
- [ ] Hot-plug works: adding or removing a VirtIO device
      in QEMU causes the matching driver to load or
      unload.
- [ ] The ARM64 build boots to a shell prompt under
      QEMU `virt` with AAVMF firmware and the PL011 UART.
- [ ] `make run-qemu-arm64` works on Windows 11 + WSL2.
- [ ] `dotnet new neutrino-console -n MyApp` creates a
      working project.
- [ ] `dotnet build -c Release` produces a `.npkg`
      package.
- [ ] `npkg sign` and `npkg publish` work from Windows
      11.
- [ ] The local repository server serves packages
      correctly.
- [ ] `npkg install MyApp` on NeutrinoOS installs the
      application, and it runs from the shell.
- [ ] `dotnet new neutrino-driver -n MyDriver` creates a
      working driver project, and the driver can be
      packaged, installed, and loaded.
- [ ] The SDK documentation allows a third-party
      developer to write, package, and publish a
      NeutrinoOS application from a fresh Windows 11
      machine.
- [ ] No C or C++ files exist in the kernel, bootloader,
      driver, `korlib`, shell, utility, SSH server, web
      server, package manager, or SDK directories.
- [ ] `docs/BUILD-WINDOWS.md` (from Phase 1) and
      `docs/PHASE2-ACCEPTANCE.md` through
      `docs/PHASE7-ACCEPTANCE.md` still work, and
      `docs/PHASE8-ACCEPTANCE.md` provides step-by-step
      verification for every checklist item above from a
      fresh Windows 11 machine.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps mapped to
   the six tasks above.
2. **Repository layout** — the target directory tree after
   Phase 8, highlighting new and modified files.
3. **Code changes** — for each file to be created,
   modified, or deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created files)
     OR a unified diff (for modifications) OR a precise
     description (for deletions).
   - For large files (e.g., the package manager, the
     driver framework, the ARM64 port), provide the
     complete source; do not abbreviate with "..." unless
     the omitted region is boilerplate that is explicitly
     described.
4. **Package format specification** — a table listing
   each field in `manifest.json`, its type, and its
   purpose.
5. **Driver ABI specification** — a table listing each
   interface and method in
   `NeutrinoOS.Driver.Abstractions.dll`, its purpose,
   and its stability guarantee.
6. **ARM64 architecture differences** — a table listing
   each subsystem (boot, exceptions, paging, interrupts,
   timer, syscalls, SMP, UART) and how it differs
   between x86-64 and ARM64.
7. **SDK template table** — a table listing each
   template, its purpose, and its output.
8. **Build and test commands** — exact WSL2 bash
   commands and PowerShell commands for Windows 11 to
   build, run, and verify Phase 8 (both x86-64 and
   ARM64).
9. **Acceptance checklist** — reproduce the checklist
   above, with a one-line note for each item explaining
   how it is satisfied.
10. **Deferred to later phases** — anything that came up
    that belongs to Phase 9+ (graphical package manager,
    public repository, USB support, community
    governance, full CI/CD pipeline, RISC-V port).
11. **Open questions / assumptions** — anything
    ambiguous about the Phase 7 output, the existing
    TCP/IP stack, the existing `korlib` structure, the
    ProtonOS conventions, or the bflat ARM64 support
    that you assumed, and how the user can verify or
    correct them.

If any part of the Phase 7 output is unclear, or if the
existing `korlib` layout or bflat ARM64 support does not
match your assumptions, state your assumptions explicitly
and proceed with a reasonable layout consistent with a
bflat-based managed kernel, noting where the user must
adjust paths.

Do not skip ahead to Phase 9+. Scope discipline is
mandatory: Phase 8 only.