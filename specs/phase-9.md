# ROLE

You are a senior systems engineer specializing in USB host controller
drivers, advanced network protocol implementations (IPv6, HTTP/2, QUIC),
ACPI power management, and physical hardware enablement for custom
operating systems. You are assisting in Phase 9 of a custom operating
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
  pipes, redirection, background execution, sequential and
  conditional execution. Built-in commands and external utilities
  (`.dll`, .NET 10) including `ls`, `cat`, `echo`, `mkdir`, `rm`,
  `cp`, `mv`, `wc`, `grep`, `ps`, `kill`, `sleep`, `df`, `mount`,
  `umount`, `uname`, `date`, `uptime`, `free`, `env`, `ifconfig`,
  `dhcp`, `ping`, `dns`, `netstat`, `wget`, `curl`, `ssh` (client),
  `gc`, `history`, `export`, `unset`.

Phase 6 status: COMPLETE. Hardened TCP/IP stack with POSIX-like
  socket API. Managed C# cryptographic primitives (SHA family,
  HMAC, AES-GCM, ChaCha20-Poly1305, ECDH, Curve25519, RSA, ECDSA,
  Ed25519, CSPRNG). TLS 1.2/1.3. SSH-2.0 server (sshd) with
  password and publickey authentication, PTY-backed shell
  sessions, and `exec`/`subsystem` requests. Minimal user
  database. .NET 10 web hosting via ported Kestrel (or fallback
  HTTP/1.1 server) with HTTP and HTTPS support. `/dev/random`.
  Minimal packet filter.

Phase 7 status: COMPLETE. Profiling infrastructure, performance
  optimizations (30% faster boot, 2x loopback TCP throughput, 30%
  faster TLS handshake, 50% faster GC gen-0 pause, 20% faster JIT
  compile), security hardening (W^X, ASLR, stack canaries, guard
  pages, syscall filtering, SSH/web rate limiting, audit logging,
  secure defaults, NIST/RFC crypto test vectors), release
  packaging (VirtualBox `.ova`, QEMU `.qcow2`, raw `.img`,
  reproducible builds, `release.json` with GPG signature,
  Windows 11 PowerShell installer).

Phase 8 status: COMPLETE. Native package manager (`npkg`) with
  `.npkg` format, repository index, dependency resolver,
  install/remove/upgrade/list/search/info/repo commands, atomic
  transactions, Ed25519 signing. Device driver framework with
  `IDevice`, `IDriver`, `IDriverHost`, `IDeviceTree`, driver
  registry, stable driver ABI, PCI/PCIe and VirtIO bus
  enumerators, hot-plug support. Ported drivers (UART, VGA text,
  PS/2 keyboard, VirtIO-Net, E1000, VirtIO-BLK, AHCI) running in
  user-mode driver hosts. ARM64 (AArch64) port booting under QEMU
  `virt` with AAVMF firmware and PL011 UART. Developer SDK with
  templates (`neutrino-console`, `neutrino-utility`,
  `neutrino-driver`, `neutrino-webapp`, `neutrino-library`),
  MSBuild packaging targets, `npkg` CLI for Windows 11, local
  repository server.

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
  - bflat (configured to target .NET 10 and ARM64)
    - NOTE: bflat is a crosscompiler that can target
      Linux/Windows/Android/UEFI x64/arm64. ARM64 UEFI support
      is available.
  - clang / ld.lld (LLVM 17+)
  - GNU make
  - Python 3.11+
  - qemu-system-x86_64 with OVMF firmware
  - qemu-system-aarch64 with AAVMF firmware
  - git
- Phase 8 boot verification used:
  `make run-qemu-vga` (x86-64) and `make run-qemu-arm64`
  (ARM64), booting to a NeutrinoOS shell on serial and VGA
  consoles.
- Windows 11 host has VirtualBox 7.x installed for manual
  verification.
- Phase 9 introduces USB, IPv6, HTTP/2, HTTP/3 (QUIC), and ACPI
  power management. The design must not introduce new native
  dependencies; all code must remain managed C#.

# PHASE 9 GOAL — "USB STACK, ADVANCED NETWORKING, AND REAL
# HARDWARE ENABLEMENT"

Transform NeutrinoOS from a system that runs in virtual machines
into a system that can run on real x86-64 and ARM64 hardware.
Phase 9 delivers four integrated capabilities:

1. **USB stack**: A complete USB host controller driver (xHCI
   for USB 3.x, EHCI for USB 2.0, UHCI/OHCI for USB 1.1),
   USB device enumeration, USB device class drivers (HID for
   keyboards and mice, mass storage for USB flash drives, CDC
   for USB serial adapters), and USB hot-plug support. This
   enables NeutrinoOS to use physical USB keyboards, mice, and
   storage devices on real hardware.

2. **Advanced networking**: IPv6 support (dual-stack with
   IPv4), HTTP/2 and HTTP/3 (QUIC) support for the web server,
   and ALPN (Application-Layer Protocol Negotiation) for TLS.
   This enables NeutrinoOS to serve modern web clients over
   HTTP/2 and HTTP/3, and to communicate over IPv6 networks.

3. **ACPI power management**: ACPI table parsing (FADT, DSDT,
   SSDT, MADT, MCFG, HPET), ACPI power off (S5), ACPI reboot,
   ACPI sleep states (S1, S3), and CPU power management
   (C-states, P-states). This enables NeutrinoOS to shut down
   and reboot cleanly on real hardware.

4. **Real hardware enablement**: Physical NIC drivers (Intel
   e1000e, Realtek RTL8168/8111, Intel i225/i226), physical
   storage drivers (NVMe, AHCI/SATA), physical serial drivers
   (16550 and PL011), and a hardware compatibility list (HCL)
   documenting tested hardware.

Phase 9 is complete when NeutrinoOS boots on a physical x86-64
machine (or a laptop) with a USB keyboard, a USB flash drive, a
physical NIC, and an NVMe or SATA SSD, reaches a shell prompt,
and can install a package, fetch a URL over IPv6, serve a web
page over HTTP/2, and shut down cleanly via `poweroff`.

Phase 9 does NOT include: a graphical installer, a display
server, GPU drivers, Wi-Fi drivers, Bluetooth drivers, or
support for every piece of hardware on the market. It is
strictly about the core hardware enablement needed to run on a
representative physical machine.

# DETAILED TASKS

## Task 1 — USB host controller drivers

Implement USB host controller drivers in C#, running in
user-mode driver hosts (per the Phase 8 driver framework).

- **xHCI (USB 3.x)**: Implement the eXtensible Host Controller
  Interface driver:
  - Register interface: MMIO BAR, capability registers, operational
    registers, runtime registers, doorbell registers.
  - Command ring, event ring, transfer rings (one per endpoint).
  - Device slot management: enable slot, address device, configure
    endpoint, reset endpoint, stop endpoint.
  - USB device enumeration: port reset, speed detection
    (Low/Full/High/SuperSpeed), device descriptor read, configuration
    descriptor read, set configuration.
  - Interrupt handling: MSI-X or MSI or legacy interrupt for event
    ring notifications.
  - Hot-plug: port status change events, port connect/disconnect.
- **EHCI (USB 2.0)**: Implement the Enhanced Host Controller
  Interface driver (required for USB 2.0 devices on systems
  without xHCI):
  - Register interface: capability registers, operational registers.
  - Asynchronous schedule (for control and bulk transfers).
  - Periodic schedule (for interrupt and isochronous transfers).
  - Queue head and queue transfer descriptor management.
  - USB device enumeration and hot-plug.
- **UHCI/OHCI (USB 1.1)**: Implement UHCI (Intel) and OHCI
  (Compaq/Microsoft) drivers for legacy USB 1.1 support. These
  are lower priority; document if deferred.
- **USB core stack**:
  - USB device model: `IUsbDevice`, `IUsbEndpoint`,
    `IUsbInterface`, `IUsbConfiguration`.
  - USB transfer types: control, bulk, interrupt, isochronous.
  - USB descriptors: device, configuration, interface, endpoint,
    string, HID, CDC.
  - USB hub driver: enumerate hubs, manage downstream ports,
    handle port status changes.
- **USB device class drivers**:
  - **HID (Human Interface Device)**: USB keyboard and mouse
    drivers. Boot protocol and report protocol. Integrate with
    the CAL as input devices (a USB keyboard appears as an
    additional input source alongside PS/2 and serial).
  - **Mass Storage**: USB Mass Storage Class (BOT — Bulk-Only
    Transport) driver for USB flash drives. Expose the device as
    a block device (`/dev/sda`, `/dev/sda1`, etc.) via the VFS.
    Support FAT32 and EXT2 filesystems on the USB drive.
  - **CDC (Communications Device Class)**: USB CDC-ACM driver
    for USB serial adapters (FTDI, Prolific, CDC-ACM). Expose
    the device as a serial character device (`/dev/ttyUSB0`).
  - **Hub**: USB hub driver for downstream port management.
- **Hot-plug**: When a USB device is plugged in or removed, the
  hub driver detects the port status change, enumerates the
  device, and loads the matching class driver. On removal, the
  driver is unloaded and the device node is removed.
- **Reference**: The xHCI specification (Intel) is the primary
  reference. The `usb.org` website provides the USB 3.2 and
  USB 2.0 specifications. For bare-metal USB implementation
  techniques, reference the STM32 USB tutorials and the
  `CShark/stm32usb` project for low-level USB device
  implementation concepts.
- **Testing**: QEMU supports USB 3.0 (xHCI) via
  `-device qemu-xhci`. Add a `make run-qemu-usb` target that
  launches QEMU with an xHCI controller and a USB keyboard
  and USB mass storage device attached. Verify that the USB
  keyboard works as an input device and that the USB flash
  drive is accessible as `/dev/sda`.
- Document the USB stack in `docs/PHASE9-USB.md`.

## Task 2 — IPv6 support

Extend the NeutrinoOS TCP/IP stack to support IPv6, dual-stack
with IPv4.

- **IPv6 layer**: Implement the IPv6 header (40 bytes),
  extension headers (Hop-by-Hop, Routing, Fragment, Destination
  Options), and the IPv6 addressing model (unicast, multicast,
  anycast).
- **ICMPv6**: Implement ICMPv6 for Neighbor Discovery (NDP —
  Neighbor Solicitation, Neighbor Advertisement, Router
  Solicitation, Router Advertisement, Redirect) and for
  `ping6`.
- **DHCPv6**: Implement a DHCPv6 client for automatic address
  configuration. Also support Stateless Address
  Autoconfiguration (SLAAC) via Router Advertisement.
- **DNS**: Extend the DNS resolver to support AAAA records
  (IPv6 addresses) alongside A records (IPv4 addresses).
- **Dual-stack sockets**: Extend the socket API to support
  `AddressFamily.InterNetworkV6` and dual-stack sockets
  (`AddressFamily.InterNetworkV6` with `IPV6_V6ONLY` disabled).
- **Utilities**: Add `ipv6` configuration to `ifconfig` (or a
  new `ifconfig6`), add `ping6` for ICMPv6 echo, add `dns6`
  for AAAA record resolution, and add `netstat6` for IPv6
  connection listing.
- **Reference**: The `smoltcp` Rust TCP/IP stack is a
  standalone, event-driven TCP/IP stack designed for bare-metal,
  real-time systems, with IPv6 support. Use it as a design
  reference for the IPv6 layer, but implement the stack
  directly in C# (do not port Rust code).
- **Testing**: QEMU user-mode networking supports IPv6. Add a
  test that boots NeutrinoOS, configures an IPv6 address via
  SLAAC or DHCPv6, and pings `::1` and a host on the IPv6
  network. Verify `curl -6 http://[::1]:5000/` works against
  the NeutrinoOS web server.
- Document the IPv6 implementation in `docs/PHASE9-IPV6.md`.

## Task 3 — HTTP/2 and HTTP/3 (QUIC) support

Extend the NeutrinoOS web server to support HTTP/2 and HTTP/3,
in addition to HTTP/1.1.

- **HTTP/2**:
  - Implement the HTTP/2 framing layer (DATA, HEADERS, PRIORITY,
    RST_STREAM, SETTINGS, PUSH_PROMISE, PING, GOAWAY,
    WINDOW_UPDATE, CONTINUATION).
  - Implement HPACK header compression (RFC 7541) with Huffman
    encoding.
  - Implement stream multiplexing, flow control, and server
    push.
  - Implement ALPN negotiation for HTTP/2 over TLS
    (`h2`) and HTTP/2 cleartext (`h2c`) with prior knowledge
    or upgrade.
  - Integrate with the Phase 6 TLS implementation.
- **HTTP/3 (QUIC)**:
  - Implement the QUIC transport protocol (RFC 9000) including
    connection establishment, stream multiplexing, flow
    control, loss recovery, and congestion control.
  - Implement TLS 1.3 handshake integration (QUIC uses TLS 1.3
    for its handshake; the Phase 6 TLS 1.3 implementation is
    the foundation).
  - Implement the HTTP/3 framing layer (RFC 9114) and QPACK
    header compression (RFC 9204).
  - Implement ALPN negotiation for HTTP/3 (`h3`).
  - QUIC requires UDP; the Phase 6 UDP implementation is the
    foundation.
- **Kestrel integration**: If Kestrel was ported in Phase 6,
  extend the Kestrel transport to use HTTP/2 and HTTP/3.
  If the fallback HTTP/1.1 server was used, extend it to
  support HTTP/2 and HTTP/3 directly.
- **Reference**: The `http2-katana` project is an implementation
  of HTTP/2 in C# with OWIN Katana, providing a reference for
  the framing and HPACK implementation. The `PicoNode.Http`
  library implements HTTP/1.1, HTTP/2 (including h2c upgrade),
  and HPACK (RFC 7541) with Huffman encoding, providing another
  reference. For QUIC, reference the QUIC RFCs and the
  `System.Net.Quic` implementation in .NET (which is
  libmsquic-based on desktop, so port the managed portions).
- **Testing**: Use `curl --http2` and `curl --http3` from
  Windows 11 to test the NeutrinoOS web server. Verify that
  HTTP/2 and HTTP/3 requests are served correctly.
- Document the HTTP/2 and HTTP/3 implementation in
  `docs/PHASE9-HTTP2-3.md`.

## Task 4 — ACPI power management

Implement ACPI table parsing and power management.

- **ACPI table parsing**:
  - RSDP (Root System Description Pointer) — already
    collected by the bootloader in Phase 1.
  - RSDT (Root System Description Table) / XSDT (Extended
    System Description Table).
  - FADT (Fixed ACPI Description Table) — for power
    management registers (PM1_CNT, PM1_STS, PM_TMR,
    etc.).
  - DSDT (Differentiated System Description Table) and
    SSDT (Secondary System Description Table) — for
    AML (ACPI Machine Language) parsing.
  - MADT (Multiple APIC Description Table) — for CPU
    enumeration and interrupt routing (already used in
    Phase 1 for SMP).
  - MCFG (Memory Mapped Configuration Table) — for PCIe
    ECAM configuration space access.
  - HPET (High Precision Event Timer) — for high-resolution
    timers.
  - Implement an AML parser and interpreter for the subset
    needed to execute `_S5` (shutdown), `_S3` (sleep), and
    `_PTS`/`_WAK` (prepare-to-sleep / wake) methods.
- **Power management**:
  - **Power off (S5)**: Write to the PM1_CNT register to
    trigger a soft power off. The `_S5` AML method may
    need to be evaluated first. This is the `poweroff`
    shell command.
  - **Reboot**: Write to the reset register (via FADT) to
    trigger a reboot. This is the `reboot` shell command.
  - **Sleep (S3)**: Enter the S3 sleep state (suspend to
    RAM). This requires saving CPU state, configuring
    wake sources, and writing to PM1_CNT. Wake from S3
    requires restoring CPU state. This is the `sleep`
    shell command (or `suspend`).
  - **CPU power management**: Implement C-states (idle
    states) and P-states (performance states) via ACPI
    `_CST` and `_PSS` methods. Use the `MONITOR` and
    `MWAIT` instructions for C-state entry on Intel CPUs.
- **Reference**: The Nyx Rust-first bare-metal OS integrates
  the ACPICA library (Intel's reference ACPI implementation)
  as a C static library and implements ACPI power off
  (`acpi::poweroff()` triggers a clean S5 shutdown) and
  WiFi power management via ACPI namespace evaluation. Use
  it as a design reference, but implement the ACPI parser
  and AML interpreter directly in C# (do not port ACPICA).
  Note that QEMU may not support ACPI power off via
  ACPICA's default mechanism; document the QEMU limitation
  and verify on VirtualBox and real hardware.
- **Testing**: Test `poweroff` and `reboot` in QEMU (may
  not work due to QEMU limitations), VirtualBox (should
  work), and real hardware (the primary target).
- Document the ACPI implementation in `docs/PHASE9-ACPI.md`.

## Task 5 — Real hardware enablement

Add drivers and support for common physical hardware.

- **Physical NIC drivers**:
  - **Intel e1000e**: The PCIe variant of the e1000
    (found on many Intel motherboards).
  - **Realtek RTL8168/8111**: The most common consumer
    NIC (found on many motherboards and laptops).
  - **Intel i225/i226**: The modern 2.5GbE NIC
    (found on newer Intel motherboards).
  - **Broadcom NetXtreme**: Found on some servers.
  - **Driver structure**: Each NIC driver implements the
    Phase 8 `IDriver` interface, registers with the
    network stack, and exposes a network interface
    (`eth0`, `eth1`, etc.).
- **Physical storage drivers**:
  - **NVMe**: The NVMe host controller driver for PCIe
    SSDs. Implement the admin queue, I/O queues, and
    the NVMe command set (identify, read, write,
    flush).
  - **AHCI/SATA**: The AHCI driver for SATA SSDs and
    HDDs (already ported in Phase 8; verify it works
    on real hardware).
  - **USB Mass Storage**: The USB mass storage driver
    from Task 1 exposes USB flash drives as block
    devices.
- **Physical serial drivers**:
  - **16550**: The standard PC serial port (already
    supported).
  - **PL011**: The ARM PrimeCell UART (already
    supported on ARM64).
  - **USB CDC-ACM**: The USB serial adapter driver
    from Task 1.
- **Hardware compatibility list (HCL)**:
  - Create `docs/HARDWARE-COMPATIBILITY.md` listing
    tested hardware (motherboards, CPUs, NICs, storage
    devices, USB devices) and their status
    (works / partially works / does not work).
  - Include the specific hardware used for testing
    (e.g., "Intel NUC 11, Intel i7-1165G7, Intel
    i225-V NIC, Samsung 980 Pro NVMe SSD").
- **Physical boot testing**:
  - Write the NeutrinoOS `.img` to a USB flash drive
    using the Phase 7 `flash-usb.ps1` script.
  - Boot the physical machine from the USB drive
    (UEFI boot).
  - Verify that NeutrinoOS boots, detects the NIC
    and storage devices, and reaches a shell prompt.
  - Document any issues encountered and the
    workarounds.
- Document the hardware enablement in
  `docs/PHASE9-HARDWARE.md`.

## Task 6 — Testing and documentation

- **Test suite**:
  - `tests/run-phase9-tests.ps1` (PowerShell for
    Windows 11) that:
    - Boots NeutrinoOS in QEMU with xHCI and USB
      devices, verifies USB keyboard and USB mass
      storage.
    - Boots NeutrinoOS in QEMU with IPv6 networking,
      verifies SLAAC/DHCPv6, `ping6`, and
      `curl -6`.
    - Boots NeutrinoOS in QEMU, verifies HTTP/2
      and HTTP/3 with `curl --http2` and
      `curl --http3`.
    - Boots NeutrinoOS in QEMU, verifies `poweroff`
      and `reboot` (with documented QEMU
      limitations).
    - Boots NeutrinoOS in VirtualBox, verifies
      `poweroff` and `reboot`.
    - Runs the full Phase 1–8 acceptance suite to
      verify no regressions.
- **Physical hardware testing**:
  - Document the physical hardware testing procedure
    in `docs/PHASE9-HARDWARE-TESTING.md`.
  - Include a checklist for testing a new machine:
    boot, USB keyboard, USB storage, NIC, storage,
    IPv6, HTTP/2, HTTP/3, `poweroff`, `reboot`.
- **Documentation**:
  - `docs/PHASE9-USB.md` — USB stack design, xHCI
    and EHCI drivers, device class drivers, hot-plug.
  - `docs/PHASE9-IPV6.md` — IPv6 implementation,
    ICMPv6, NDP, DHCPv6, SLAAC, dual-stack sockets.
  - `docs/PHASE9-HTTP2-3.md` — HTTP/2 framing, HPACK,
    QUIC, HTTP/3 framing, QPACK, ALPN.
  - `docs/PHASE9-ACPI.md` — ACPI table parsing, AML
    interpreter, power management (S5, S3), CPU
    C-states and P-states.
  - `docs/PHASE9-HARDWARE.md` — physical NIC, storage,
    and serial drivers, HCL.
  - `docs/HARDWARE-COMPATIBILITY.md` — tested hardware.
  - `docs/PHASE9-ACCEPTANCE.md` — step-by-step
    verification for every acceptance criterion
    below from a fresh Windows 11 machine.
  - `PHASE9-REPORT.md` — summary of changes,
    blockers, deviations.

# CONSTRAINTS

- All code must be C# (plus the existing assembly
  intrinsics). Do NOT add C or C++ files to the kernel,
  bootloader, drivers, `korlib`, shell, utilities, SSH
  server, web server, package manager, driver framework,
  or SDK.
- Do NOT introduce a graphical framebuffer, GUI, mouse
  support (beyond HID mouse input for terminal cursor
  positioning), or a window manager. This phase is
  strictly console-only.
- Do NOT depend on OpenSSL, libssh, libcurl, libusb,
  libmsquic, or any other native library. All
  cryptography, TLS, SSH, HTTP/2, HTTP/3, USB, ACPI,
  and IPv6 must be implemented in managed C#.
- Do NOT implement Wi-Fi drivers, Bluetooth drivers,
  or GPU drivers. These are out of scope for Phase 9
  and belong to Phase 10+ (if ever).
- Do NOT implement a graphical installer. The
  Windows 11 installer from Phase 7 is sufficient.
- Do NOT implement a public repository or a graphical
  package manager. These are out of scope.
- Do NOT use the term "TTY" as a project name or
  suffix. It is fine to use the Unix term "tty" in
  device paths (`/dev/ttyS0`, `/dev/ttyUSB0`) and
  documentation.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to
  ProtonOS.
- Do NOT scope-creep into Phase 10+ (Wi-Fi, Bluetooth,
  GPU, graphical installer, public repository,
  RISC-V port, external security audit).
- Every public type and method added to `korlib`, the
  kernel, the USB stack, the network stack, the web
  server, or the driver framework must have XML doc
  comments describing its Phase 9 semantics.
- All user-visible strings must say "NeutrinoOS".

# DELIVERABLES

1. USB host controller drivers (xHCI, EHCI) with USB
   device enumeration, USB hub driver, and USB device
   class drivers (HID, mass storage, CDC-ACM) running
   in user-mode driver hosts.
2. IPv6 support in the TCP/IP stack (IPv6 header,
   extension headers, ICMPv6, NDP, DHCPv6, SLAAC,
   dual-stack sockets) with `ping6`, `dns6`, and
   `netstat6` utilities.
3. HTTP/2 and HTTP/3 (QUIC) support in the web server
   (HTTP/2 framing, HPACK, QUIC transport, HTTP/3
   framing, QPACK, ALPN) with `curl --http2` and
   `curl --http3` testing.
4. ACPI power management (ACPI table parsing, AML
   interpreter, `poweroff`, `reboot`, `sleep`, CPU
   C-states and P-states).
5. Physical NIC drivers (Intel e1000e, Realtek
   RTL8168/8111, Intel i225/i226), physical storage
   drivers (NVMe, AHCI), and physical serial drivers
   (16550, PL011, USB CDC-ACM).
6. `docs/HARDWARE-COMPATIBILITY.md` listing tested
   hardware and their status.
7. Test suite: `tests/run-phase9-tests.ps1`,
   `docs/PHASE9-HARDWARE-TESTING.md`.
8. Documentation: `docs/PHASE9-USB.md`,
   `docs/PHASE9-IPV6.md`, `docs/PHASE9-HTTP2-3.md`,
   `docs/PHASE9-ACPI.md`, `docs/PHASE9-HARDWARE.md`,
   `docs/PHASE9-ACCEPTANCE.md`, `PHASE9-REPORT.md`.

# ACCEPTANCE CRITERIA

Phase 9 is complete when ALL of the following are true:

- [ ] `make image` and `make run-qemu-vga` still boot to a
      NeutrinoOS banner on both consoles, with no
      regressions from Phase 8.
- [ ] `make run-qemu-usb` boots NeutrinoOS with an xHCI
      controller, and a USB keyboard works as an input
      device.
- [ ] A USB mass storage device appears as `/dev/sda`,
      and its FAT32 or EXT2 filesystem can be mounted
      and accessed.
- [ ] A USB CDC-ACM device appears as `/dev/ttyUSB0`,
      and data can be read from and written to it.
- [ ] USB hot-plug works: plugging in and removing a
      USB device causes the matching driver to
      load/unload and the device node to appear/disappear.
- [ ] `ifconfig` shows an IPv6 address (via SLAAC or
      DHCPv6) in addition to the IPv4 address.
- [ ] `ping6 ::1` succeeds.
- [ ] `curl -6 http://[::1]:5000/` fetches a page from
      the NeutrinoOS web server over IPv6.
- [ ] `curl --http2 https://neutrinoos-ip:5001/`
      fetches a page over HTTP/2.
- [ ] `curl --http3 https://neutrinoos-ip:5001/`
      fetches a page over HTTP/3 (QUIC).
- [ ] `poweroff` shuts down the system cleanly on
      VirtualBox and real hardware (QEMU may not
      support ACPI power off; document the
      limitation).
- [ ] `reboot` reboots the system cleanly on
      VirtualBox and real hardware.
- [ ] `sleep` enters the S3 sleep state and wakes
      on a configured wake source (may be deferred;
      document if not implemented).
- [ ] CPU C-states and P-states are detected and
      reported via `cpupower` (or a similar command).
- [ ] NeutrinoOS boots on at least one physical
      x86-64 machine with a USB keyboard, a physical
      NIC, and an NVMe or SATA SSD, and reaches a
      shell prompt.
- [ ] NeutrinoOS boots on at least one physical
      ARM64 machine (e.g., Raspberry Pi 4/5,
      Rockchip RK3399) with a USB keyboard and a
      physical NIC, and reaches a shell prompt
      (may be deferred if no ARM64 hardware is
      available; document the limitation).
- [ ] `docs/HARDWARE-COMPATIBILITY.md` lists all
      tested hardware with its status.
- [ ] No C or C++ files exist in the kernel,
      bootloader, driver, `korlib`, shell, utility,
      SSH server, web server, package manager,
      driver framework, or SDK directories.
- [ ] `docs/BUILD-WINDOWS.md` (from Phase 1) and
      `docs/PHASE2-ACCEPTANCE.md` through
      `docs/PHASE8-ACCEPTANCE.md` still work, and
      `docs/PHASE9-ACCEPTANCE.md` provides
      step-by-step verification for every checklist
      item above from a fresh Windows 11 machine.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps mapped
   to the six tasks above.
2. **Repository layout** — the target directory tree
   after Phase 9, highlighting new and modified
   files.
3. **Code changes** — for each file to be created,
   modified, or deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created
     files) OR a unified diff (for modifications)
     OR a precise description (for deletions).
   - For large files (e.g., the xHCI driver, the
     IPv6 stack, the QUIC implementation, the ACPI
     parser), provide the complete source; do not
     abbreviate with "..." unless the omitted
     region is boilerplate that is explicitly
     described.
4. **USB stack architecture** — a table listing each
   component (xHCI, EHCI, hub, HID, mass storage,
   CDC), its responsibilities, and its status.
5. **IPv6 implementation table** — a table listing
   each IPv6 feature (header, extension headers,
   ICMPv6, NDP, DHCPv6, SLAAC, dual-stack sockets)
   and its status.
6. **HTTP/2 and HTTP/3 feature table** — a table
   listing each protocol feature (framing, HPACK,
   QPACK, QUIC transport, ALPN) and its status.
7. **ACPI feature table** — a table listing each
   ACPI table (FADT, DSDT, SSDT, MADT, MCFG, HPET),
   its purpose, and its status.
8. **Hardware compatibility list** — a table
   listing tested hardware, its category (CPU,
   motherboard, NIC, storage, USB), and its
   status.
9. **Build and test commands** — exact WSL2 bash
   commands and PowerShell commands for Windows 11
   to build, run, and verify Phase 9 (x86-64,
   ARM64, QEMU, VirtualBox, and physical hardware).
10. **Acceptance checklist** — reproduce the
    checklist above, with a one-line note for each
    item explaining how it is satisfied.
11. **Deferred to later phases** — anything that
    came up that belongs to Phase 10+ (Wi-Fi,
    Bluetooth, GPU, graphical installer, public
    repository, RISC-V port, external security
    audit, IPv6 over low-power wireless, full
    ACPI S3/S4 support).
12. **Open questions / assumptions** — anything
    ambiguous about the Phase 8 output, the
    existing driver framework, the existing
    TCP/IP stack, the existing web server, the
    ProtonOS conventions, or the bflat ARM64
    support that you assumed, and how the user
    can verify or correct them.

If any part of the Phase 8 output is unclear, or if
the existing driver framework, TCP/IP stack, or web
server does not match your assumptions, state your
assumptions explicitly and proceed with a reasonable
layout consistent with a bflat-based managed kernel,
noting where the user must adjust paths.

Do not skip ahead to Phase 10+. Scope discipline is
mandatory: Phase 9 only.