# NeutrinoOS: A Console-Only Fork of ProtonOS for .NET 10

## Design Document v0.1

---

## 1. Executive Summary

**NeutrinoOS** is a fork of ProtonOS designed for headless, console-only operation. It removes all graphical subsystem dependencies and framebuffer initialization, replacing them with a pure serial/TTY-based console model. The system retains ProtonOS's core architecture—a hybrid managed kernel written in C# using `bflat`'s zero-library mode, with a Tier-0 JIT compiler for dynamic .NET assembly loading—while targeting applications and utilities written for **.NET 10**.

The name "NeutrinoOS" reflects the design philosophy: like a neutrino, the system is elusive, non-interactive, and passes through its work without a graphical trace. It exists to serve applications, not to display them.

The design philosophy is simple: **one console, one purpose**. There is no window manager, no framebuffer driver, and no graphics stack. Every interaction occurs through a serial terminal or virtual console, making the system ideal for embedded appliances, CI/CD build nodes, network services, and educational platforms for managed OS development.

---

## 2. Core Architecture: What Is Retained from ProtonOS

The following core systems from ProtonOS are retained unchanged, as they are already console-oriented and have no graphical dependencies.

### 2.1 Hybrid Managed Kernel (ManagedKernel)

NeutrinoOS inherits ProtonOS's hybrid kernel architecture:

- **Minimal Native Layer**: Approximately 150–200 lines of assembly per architecture, providing CPU intrinsics for privileged instructions (control register access, port I/O, interrupt enable/disable). This layer is identical to ProtonOS's.
- **AOT-Compiled Managed Kernel**: Core kernel services—memory management, scheduling, interrupt handling—are written in C# and compiled ahead-of-time using `bflat` / .NET NativeAOT. No C code exists in the kernel.
- **Architecture Independence**: A Hardware Abstraction Layer (HAL) defines common interfaces, with architecture-specific code isolated in separate modules (`Kernel.Arch.X64`, `Kernel.Arch.Arm64`).

ProtonOS already has a **"Linux-Like Console Model"** as a stated design principle: "Serial/TTY-style output from earliest boot" and "Virtual terminals with framebuffer (later)." NeutrinoOS makes this model the *only* model, removing the "framebuffer (later)" portion.

### 2.2 Compacting Garbage Collector

ProtonOS's **Mark-Sweep Compacting GC** with **Lisp-2 compaction** and a **Large Object Heap (LOH)** is retained. This GC is essential for running .NET applications that allocate and free memory dynamically.

### 2.3 Preemptive Scheduler with SMP and NUMA

The **preemptive scheduler** with per-CPU run queues, **APIC timer**, **SMP boot** for multi-processor systems, and **NUMA topology detection** are all retained. These are critical for running concurrent .NET applications and services.

### 2.4 Virtual Memory and Syscall Interface

ProtonOS's **4-level paging** with higher-half kernel, **Ring 3 execution**, and **Linux-compatible ABI** (x86-64 `syscall`/`sysret` with Linux syscall numbers, 50+ syscalls) are retained. This provides the user-mode environment necessary for running managed applications safely.

### 2.5 Tier-0 JIT Compiler

ProtonOS's **Tier-0 JIT compiler** is the heart of the dynamic execution model. It supports generics, delegates, interfaces, and reflection, and enables **cross-assembly loading**—the ability to load and link multiple .NET assemblies at runtime. It also provides **AOT↔JIT interop**, allowing seamless calls between AOT kernel code and JIT-compiled drivers.

---

## 3. What Is Removed: Graphical Subsystem

The following ProtonOS components are removed or disabled in NeutrinoOS.

| Component | Reason for Removal |
| :--- | :--- |
| **Framebuffer Driver** | No graphical output. All display is through serial or text-mode VGA console. |
| **Virtual Terminal (GUI)** | No windowed terminals. A single text console is used. |
| **Graphics Initialization** | UEFI boot does not request or use a Graphics Output Protocol (GOP) framebuffer. |
| **Window Manager** | Does not exist in ProtonOS, but any future GUI extensions are explicitly out of scope. |

The UEFI bootloader is simplified: it no longer needs to locate or configure a linear framebuffer. Instead, it initializes the **serial port** (typically COM1 at 0x3F8) for early boot output and continues using that as the primary console.

---

## 4. Console Subsystem Design

### 4.1 Serial Console (Primary)

The primary console is a **serial TTY** operating over UART. This is the earliest and most reliable output method, available from the first instruction after boot.

**Specifications:**
- **Port**: COM1 (0x3F8) by default, configurable via boot parameters
- **Baud Rate**: 115200 (configurable: 9600, 19200, 38400, 57600, 115200)
- **Data Format**: 8N1 (8 data bits, no parity, 1 stop bit)
- **Protocol**: Raw serial, with ANSI escape sequence support for cursor movement and color

**Driver**: A minimal UART driver written in C# using `unsafe` code and `UnmanagedCallersOnly` for port I/O. The driver registers with the kernel's device framework and exposes a character device at `/dev/ttyS0`.

### 4.2 Virtual Console (Secondary)

After the kernel fully initializes, a **text-mode VGA console** (80×25 or 80×50 characters) is initialized as a fallback for local access. This uses the standard VGA text buffer at physical address `0xB8000`. No graphical framebuffer is initialized.

**Driver**: A VGA text-mode driver written in C#, writing directly to the VGA text buffer. It supports:
- 16 foreground/background color combinations
- Hardware cursor positioning
- Scrolling (hardware or software)

### 4.3 Console Abstraction Layer

A **Console Abstraction Layer (CAL)** provides a unified interface to all console devices. Applications call `System.Console.Write` or `System.Console.WriteLine`, which routes to the CAL, which then dispatches to the active console device (serial, VGA, or both simultaneously).

**API surface:**
```
IConsoleDevice
├── Write(char c)
├── Write(string s)
├── ReadKey() -> ConsoleKeyInfo
├── Clear()
├── SetCursorPosition(int x, int y)
├── ForegroundColor / BackgroundColor
└── Flush()
```

The CAL is implemented in C# as part of `korlib` (see Section 5), providing the `System.Console` types that .NET applications expect.

---

## 5. Running .NET 10 Applications

### 5.1 Runtime Architecture

NeutrinoOS runs .NET 10 applications through the same dual-engine model as ProtonOS:

1. **AOT-Compiled Kernel**: The kernel itself is compiled with `bflat` using `--stdlib:zero`, producing a native UEFI executable with no runtime dependencies. `bflat`'s `Zero` standard library provides "a minimal standard library that doesn't have much more than just primitive types," and is the only mode that supports x64 UEFI targets.
2. **Tier-0 JIT Compiler**: A custom JIT compiler loads and executes standard .NET assemblies (`.dll` files) at runtime. It supports generics, delegates, interfaces, and reflection.
3. **Cross-Assembly Loading**: Multiple .NET assemblies can be loaded and linked at runtime, enabling modular application deployment.

### 5.2 .NET 10 Console Application Support

Console applications targeting **.NET 10** are compiled on a development machine using the **.NET 10 SDK** and deployed to NeutrinoOS as managed assemblies. The JIT compiler then executes them.

**Compilation workflow:**
```bash
# On development machine (Linux, macOS, or Windows)
dotnet build MyConsoleApp.csproj -c Release -f net10.0

# The output DLL is copied to the NeutrinoOS filesystem
# e.g., /apps/myconsoleapp.dll
```

On NeutrinoOS, the application is launched via the terminal shell:
```
> run /apps/myconsoleapp.dll
```

**Supported .NET 10 features:**
- Console I/O (`System.Console`)
- File I/O (`System.IO`) via the VFS and FAT32/EXT2 drivers
- Networking (`System.Net.Sockets`) via the TCP/IP stack
- Threading (`System.Threading`) via the preemptive scheduler
- Generics, LINQ, and reflection (via the JIT compiler's support)
- Exception handling (`try`/`catch`/`finally`) via funclet-based unwinding

**Limitations:**
- **No GUI APIs**: `System.Windows.Forms`, `WPF`, and `Avalonia` are not supported.
- **No ASP.NET Core**: Kestrel requires a full socket API that may not be complete; a lightweight custom HTTP server is provided instead.
- **Native AOT**: Applications can also be AOT-compiled with `bflat` for better performance, but the primary deployment model is JIT execution of standard assemblies.

### 5.3 `korlib`: The Custom .NET Runtime Library

ProtonOS's `korlib` provides the minimal .NET runtime needed for managed code execution. It includes collections (`List<T>`, `Dictionary<K,V>`, `StringBuilder`), string encoding (ASCII, Unicode), and basic I/O types.

NeutrinoOS extends `korlib` with console-specific types:

- **Core collections**: `List<T>`, `Dictionary<K,V>`, `StringBuilder`
- **String encoding**: ASCII and Unicode
- **Console types**: `Console`, `TextWriter`, `TextReader`
- **File I/O**: `File`, `Directory`, `FileStream`, `StreamReader`, `StreamWriter`
- **Networking**: `Socket`, `TcpClient`, `TcpListener`, `Dns`
- **Threading**: `Thread`, `Mutex`, `Semaphore`

`korlib` is compiled with `bflat --stdlib:zero`, which provides only primitive types and avoids the large dependency tree of the full .NET standard library.

---

## 6. Terminal Shell and Utilities

### 6.1 Shell Design

NeutrinoOS includes a built-in terminal shell, written in C# and compiled AOT as part of the kernel or loaded as a JIT-compiled application. The shell provides:

- **Command parsing**: Supports arguments, pipes (`|`), redirection (`>`, `>>`, `<`), and background execution (`&`)
- **Built-in commands**: `cd`, `ls`, `cat`, `echo`, `mkdir`, `rm`, `cp`, `mv`, `ps`, `kill`, `ifconfig`, `ping`, `wget`, `curl` (HTTP/1.1 client), `ssh` (via wolfSSH), `gc` (trigger GC), `mount`, `umount`, `df`
- **External applications**: Any `.dll` in `/apps` or `/bin` can be executed

**Shell prompt:**
```
NeutrinoOS v1.0 [x86-64] (ttyS0)
user@neutrinoos:/$
```

### 6.2 Utilities (C# / .NET 10)

NeutrinoOS ships with a set of utilities written in C# and compiled for .NET 10. These are deployed as managed assemblies and executed by the JIT compiler.

| Utility | Description | Source |
| :--- | :--- | :--- |
| `ls` | List directory contents | Built-in / C# |
| `cat` | Concatenate and print files | Built-in / C# |
| `curl` | HTTP/1.1 client for fetching URLs | Based on ProtonOS HTTP library |
| `ssh` | SSH client (connect to remote hosts) | wolfSSH or CycloneSSH port |
| `ping` | ICMP echo request | ProtonOS network stack |
| `ifconfig` | Network interface configuration | ProtonOS network manager |
| `dhcp` | DHCP client (obtain IP address) | ProtonOS DHCP client |
| `dns` | DNS resolution utility | ProtonOS DNS resolver |
| `gc` | Manual garbage collection trigger | `korlib` GC API |
| `netstat` | Network connection status | ProtonOS network stack |

### 6.3 SSH Server (Optional)

NeutrinoOS can optionally include an **SSH server** based on **wolfSSH** or **CycloneSSH**, allowing remote terminal access. This is particularly useful for headless deployments where the serial console is not physically accessible.

The SSH server runs as a JIT-compiled .NET application, binding to port 22 (configurable). It uses the TCP/IP stack and provides a PTY-like interface backed by the Console Abstraction Layer.

---

## 7. Networking Stack

NeutrinoOS retains ProtonOS's complete TCP/IP stack:

- **Ethernet**, **ARP**, **IPv4**, **ICMP (ping)**, **UDP**, **TCP**
- **DHCP Client** with full lease lifecycle (T1/T2 renewal, rebinding)
- **DNS Resolution** via UDP queries
- **TCP Client** with connection management and graceful close
- **HTTP/1.1 Client** library for web requests

**Network configuration** is read from `/etc/network/interfaces` (INI format), supporting static IP configuration and DHCP.

**VirtualBox NIC support**: The design targets the **Intel E1000** (default VirtualBox adapter) and **Virtio-Net** (paravirtualized, recommended for performance). Driver implementations for both are inherited from ProtonOS's VirtIO framework.

---

## 8. Filesystem

NeutrinoOS uses ProtonOS's **Virtual File System (VFS)** abstraction with the following concrete filesystems:

- **FAT32**: Full read/write support (for the boot ESP and general storage)
- **EXT2**: Full read/write support (for Linux-compatible storage)
- **exFAT**: Read-only or read/write (requires porting or implementing a driver; ProtonOS currently supports FAT32 and EXT2)

The VFS provides mount points, path resolution, and a unified file API. Applications access files through `System.IO` types (`File`, `Directory`, `FileStream`), which the `korlib` runtime maps to VFS calls.

**Directory structure:**
```
/
├── apps/           # .NET 10 applications (.dll)
├── bin/            # System utilities (.dll)
├── drivers/        # JIT-loaded device drivers (.dll)
├── etc/
│   └── network/
│       └── interfaces
├── dev/
│   ├── ttyS0       # Serial console
│   └── vga0        # VGA text console
├── mnt/            # Mount points
└── tmp/            # Temporary files
```

---

## 9. Boot Process

The boot sequence is streamlined for console-only operation:

1. **UEFI Firmware** initializes and loads `BOOTX64.EFI` from the ESP (FAT32).
2. **UEFI Bootloader** (two-stage):
   - Stage 1: Loads the kernel ELF binary, builds identity-mapped page tables, collects UEFI memory map and ACPI RSDP.
   - Stage 2: Calls `ExitBootServices()`, sets up serial port (COM1, 115200 8N1) for early output, jumps to kernel entry point.
3. **Kernel Initialization**:
   - GDT, IDT, exception handlers
   - Physical Memory Manager (PMM) from UEFI memory map
   - Kernel heap allocator
   - Virtual Memory Manager (4-level paging, higher-half kernel)
   - Preemptive scheduler (APIC timer)
   - Compacting GC initialization
   - Serial console driver (`/dev/ttyS0`)
   - VGA text console driver (`/dev/vga0`)
4. **Driver Loading**: JIT compiler loads drivers from `/drivers`:
   - VirtIO / E1000 network driver
   - VirtIO-BLK / AHCI storage driver
   - PS/2 keyboard driver (for local VGA console input)
5. **Network Initialization**: DHCP client obtains IP address, DNS resolver configured.
6. **Shell Launch**: The terminal shell is started on `/dev/ttyS0` (serial console). If a local console is present, a second shell instance is started on `/dev/vga0`.

**Boot output (serial):**
```
NeutrinoOS v1.0 (x86-64 UEFI)
[BOOT] Loading kernel...
[BOOT] Memory: 2048 MB available
[KERNEL] GDT/IDT initialized
[KERNEL] PMM: 524288 pages (2048 MB)
[KERNEL] VMM: 4-level paging, higher-half kernel
[KERNEL] Scheduler: APIC timer @ 100 Hz, 4 CPUs detected
[GC] Compacting GC initialized (LOH: 16 MB)
[CONSOLE] Serial console initialized (ttyS0 @ 115200 8N1)
[CONSOLE] VGA text console initialized (80x50)
[DRIVER] Loading virtio-net...
[NET] eth0: DHCP lease obtained (192.168.1.100/24)
[NET] DNS: 192.168.1.1
[SHELL] NeutrinoOS shell ready.
user@neutrinoos:/$
```

---

## 10. Build System

The build system is inherited from ProtonOS:

- **Toolchain**: `bflat` (based on LLVM) with `--stdlib:zero` for AOT compilation; .NET 10 SDK for compiling user applications. `bflat` is a crosscompiler that can target x64 UEFI with `--stdlib:zero`.
- **Compiler**: `bflat` targets `x86_64-unknown-none-elf` for the kernel and `x86_64-unknown-windows` for the UEFI bootloader.
- **Build System**: `make` or a Python script orchestrating compilation and disk image creation.

**Build targets:**
```
make kernel        # Build AOT kernel
make bootloader    # Build UEFI bootloader
make drivers       # Build JIT-loadable drivers
make utilities     # Build .NET 10 utilities (ls, cat, curl, etc.)
make image         # Create bootable .vdi / .img disk image
make run           # Launch in QEMU or VirtualBox
```

**Development environment setup:**
```bash
# Install .NET 10 SDK
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0

# Install bflat
dotnet tool install -g bflat

# Clone NeutrinoOS
git clone https://github.com/neutrinoos/NeutrinoOS.git
cd NeutrinoOS

# Build and run
make image && make run
```

---

## 11. VirtualBox Compatibility

NeutrinoOS targets VirtualBox with the following configuration:

| Setting | Value |
| :--- | :--- |
| **System Type** | Other/Unknown (64-bit) |
| **Firmware** | UEFI |
| **Chipset** | ICH9 |
| **Processors** | 1–4 (SMP supported) |
| **Memory** | 512 MB minimum, 2 GB recommended |
| **Storage** | VirtIO-BLK or AHCI/SATA (VDI disk image) |
| **Network** | Intel PRO/1000 MT Desktop (E1000) or VirtIO-Net |
| **Serial Port** | Enabled, COM1, 115200 baud (for console output) |
| **Graphics** | VMSVGA (text-mode only) |

**Serial console access in VirtualBox:**
```bash
# On the host, connect to the serial port
VBoxManage modifyvm "NeutrinoOS" --uart1 0x3F8 4 --uartmode1 file /tmp/neutrinoos.log
# Or use socat for interactive access:
socat UNIX-CONNECT:/tmp/neutrinoos.sock STDIO
```

---

## 12. Development Roadmap

| Phase | Goal | Duration (est.) | Key Deliverables |
| :--- | :--- | :--- | :--- |
| **1** | **Fork & Strip** | 2–4 weeks | Fork ProtonOS, remove framebuffer/GOP init, simplify bootloader for serial-only output. |
| **2** | **Serial Console** | 2–3 weeks | UART driver in C#, Console Abstraction Layer, `System.Console` in `korlib`. |
| **3** | **VGA Text Console** | 1–2 weeks | VGA text-mode driver (80×50), keyboard input for local console. |
| **4** | **.NET 10 JIT Validation** | 3–4 weeks | Verify Tier-0 JIT executes .NET 10 assemblies; port `System.Console`, `System.IO`, `System.Net`. |
| **5** | **Shell & Utilities** | 4–6 weeks | Terminal shell, built-in commands, C# utilities (`ls`, `cat`, `curl`, `ssh`). |
| **6** | **Networking & SSH** | 3–4 weeks | DHCP, DNS, TCP client validation; optional SSH server (wolfSSH/CycloneSSH). |
| **7** | **VirtualBox Testing** | 2–3 weeks | Full boot and application execution in VirtualBox with E1000 and VirtIO-Net. |
| **8** | **Documentation & Release** | 2–3 weeks | Build instructions, API docs, sample applications, v1.0 release. |

**Total estimated effort**: 19–29 weeks (approximately 5–7 months) for a single experienced developer familiar with OS development, C#, and the ProtonOS codebase.

---

## 13. Summary

NeutrinoOS is a **console-only fork of ProtonOS** that removes all graphical dependencies while retaining the core managed kernel, Tier-0 JIT compiler, compacting GC, preemptive scheduler, TCP/IP stack, and filesystem support. It runs **.NET 10 console applications** and utilities through a custom JIT compiler and a minimal runtime library (`korlib`), with a terminal shell providing command-line interaction over serial or VGA text consoles.

The system is designed to boot in **VirtualBox** with UEFI firmware, targeting the E1000 network adapter and VirtIO-BLK storage. It is a practical platform for headless .NET application hosting, CI/CD build nodes, embedded appliances, and education in managed OS development.

The most significant challenge is **porting or verifying .NET 10 compatibility** for the JIT compiler, as ProtonOS targets .NET 10 but its JIT may not support every language feature. Early validation of `System.Console`, `System.IO`, and `System.Net.Sockets` is critical before proceeding with shell and utility development.

