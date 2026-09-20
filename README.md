# NeutrinoOS

**NeutrinoOS is a console-only, headless fork of [ProtonOS](https://github.com/ProtonOS/ProtonOS)** - a bare-metal operating system written entirely in C#, targeting x86-64 UEFI systems. NeutrinoOS removes every graphical concern (no GOP, no framebuffer, no window manager) and exposes the system exclusively through a serial console. The managed kernel, Tier-0 JIT compiler, compacting GC, preemptive scheduler, TCP/IP stack, and filesystems are retained from ProtonOS.

Like a neutrino, the system is meant to be elusive and unobtrusive: it exists to run applications, not to display them.

## The Idea

**C is unnecessary for OS development.** Everything traditionally done in C can be done in C# with `unsafe`, while privileged CPU instructions require assembly regardless of your systems language. ProtonOS proves this by implementing a complete kernel in managed code - NeutrinoOS carries that idea into headless territory.

## Fork Origin & Phase 1 Scope

- **Fork origin:** [`ProtonOS/ProtonOS`](https://github.com/ProtonOS/ProtonOS) (AGPL-3.0), based on commit `c4f6db2`. Original license and copyright notices are preserved - see [NOTICE](NOTICE).
- **Phase 1 ("Fork & Strip") scope:**
  - No UEFI Graphics Output Protocol (GOP) usage anywhere in the boot path.
  - No framebuffer or graphics initialization. The only console is serial (COM1, 0x3F8, 115200 8N1); a VGA text-mode console is deferred to a later phase.
  - All bootloader and kernel log output appears on the serial console.
  - The system reaches an interactive `neutrinoos>` prompt that echoes typed characters.
  - Build targets: `make kernel`, `make bootloader`, `make image` (produces `build/x64/neutrinoos.img`), `make run-qemu`, `make run-vbox`.
- Console-only is a hard constraint: GUI APIs (`System.Windows.Forms`, WPF, Avalonia) are excluded from the project's scope.

See `specs/` for the design documents, `docs/BUILD-WINDOWS.md` to build from Windows 11 + WSL2, and [PHASE1-REPORT.md](PHASE1-REPORT.md) for exactly what changed in the fork.

## Features

### Core Kernel
- **UEFI Boot** - Two-stage bootloader with predictable kernel load addresses
- **Custom Runtime (korlib)** - Minimal .NET runtime with collections (List, Dictionary, StringBuilder)
- **Compacting GC** - Mark-sweep with Lisp-2 compaction, Large Object Heap (LOH)
- **Full Exception Handling** - try/catch/finally/filter with funclet-based unwinding
- **SMP Support** - Multi-processor boot with per-CPU scheduling
- **NUMA Awareness** - Topology detection and NUMA-aware memory allocation
- **Preemptive Scheduler** - Multi-threaded with APIC timer, per-CPU run queues, thread cleanup
- **Virtual Memory** - 4-level paging with higher-half kernel

### JIT Compiler
- **Tier 0 JIT** - Full IL compiler supporting generics, delegates, interfaces, reflection
- **Cross-Assembly Loading** - Load and link multiple .NET assemblies at runtime
- **AOT↔JIT Interop** - Seamless calls between AOT kernel code and JIT-compiled drivers
- **GDB Debugging** - JIT methods visible in GDB via JIT interface

### Device Drivers
- **VirtIO** - Common infrastructure, virtio-blk (storage), virtio-net (networking)
- **AHCI/SATA** - Native SATA controller support
- **Dynamic Loading** - Drivers loaded from /drivers at runtime with lifecycle management

### Filesystems
- **FAT32** - Full read/write support
- **EXT2** - Full read/write support
- **VFS** - Virtual filesystem abstraction with mount support

### Networking
- **Network Stack** - Ethernet, ARP, IPv4, ICMP (ping), UDP, TCP
- **Network Manager** - Interface naming (eth0, wifi0), configuration management
- **DHCP Client** - Full lease lifecycle with T1/T2 renewal and rebinding
- **DNS Resolution** - Hostname-to-IP resolution via UDP queries
- **TCP Client** - Connection management, data transmission, graceful close
- **HTTP/1.1** - Client library for HTTP requests over TCP
- **Config Files** - INI-based network configuration from `/etc/network/interfaces`

### Syscall Interface
- **Linux-Compatible ABI** - x86-64 syscall/sysret with Linux syscall numbers
- **Ring 3 Execution** - User mode process execution with privilege separation
- **50+ Syscalls** - File I/O, memory management, process control, networking

### Debugging
- **GDB Support** - Automatic symbol loading for AOT and JIT code
- **JIT Debugging** - Set breakpoints in JIT-compiled methods by name
- **Reflection API** - Type introspection, method enumeration, dynamic invocation

## Current Status

| Component | Status |
|-----------|--------|
| UEFI boot, serial console | Complete |
| GDT/IDT, interrupts, exceptions | Complete |
| Physical/virtual memory management | Complete |
| Kernel heap allocator | Complete |
| APIC timer, HPET, RTC | Complete |
| SMP boot (multi-processor) | Complete |
| NUMA topology detection | Complete |
| Preemptive threading (per-CPU queues) | Complete |
| Compacting GC with LOH | Complete |
| Exception handling (try/catch/finally/filter) | Complete |
| Tier 0 JIT compiler | Complete |
| PE/Metadata reader | Complete |
| Assembly loading and execution | Complete |
| Cross-assembly type resolution | Complete |
| Reflection API (types, methods, fields, invoke) | Complete |
| Driver Development Kit (DDK) | Complete |
| VirtIO-blk driver (block storage) | Complete |
| VirtIO-net driver (networking) | Complete |
| AHCI/SATA driver | Complete |
| FAT32 filesystem (read/write) | Complete |
| EXT2 filesystem (read/write) | Complete |
| VFS with mount support | Complete |
| TCP/IP network stack | Complete |
| Network manager (interface naming) | Complete |
| DHCP client (with lease renewal) | Complete |
| DNS resolution | Complete |
| HTTP/1.1 client | Complete |
| Network config files | Complete |
| GDB debugging support | Complete |
| Syscall interface (50+ syscalls) | Complete |
| Ring 3 user mode execution | Complete |
| Userspace process loading | In Progress |

### Test Results

The kernel runs comprehensive test suites on boot: **3,050+ tests passing**

- **2,983** JIT/runtime tests (JITTest) - IL opcodes, features, korlib APIs, regression tests
- **50** Ring 3 syscall tests - User mode syscall validation
- **17** application-level tests (HTTP, DNS, DHCP, filesystem, /proc)

### Supported C# Features

| Category | Features |
|----------|----------|
| **Basic Operations** | All arithmetic, bitwise, comparison, and conversion operations |
| **Control Flow** | if/else, switch, for/while/do loops, goto |
| **Methods** | Static, instance, virtual, interface dispatch, delegates |
| **Exception Handling** | try/catch/finally, throw/rethrow, filters (`catch when`), nested handlers |
| **Object Model** | Classes, structs, interfaces, inheritance, boxing/unboxing |
| **Arrays** | Single-dimension, multi-dimension (2D/3D), jagged, bounds checking |
| **Generics** | Generic classes, methods, interfaces, delegates, constraints, variance |
| **Delegates** | Static/instance, multicast (Combine/Remove), closures/lambdas |
| **Value Types** | Structs (all sizes), Nullable<T> with lifted operators, explicit layout |
| **Reflection** | typeof, GetType, GetMethods/Fields/Constructors, MethodInfo.Invoke |
| **Unsafe Code** | Pointers, stackalloc, fixed buffers, calli, function pointers |
| **Threading** | Interlocked operations, thread APIs (via kernel exports) |
| **Resource Management** | IDisposable, using statement, foreach on arrays and collections |
| **Collections** | List\<T\>, Dictionary\<TKey,TValue\>, StringBuilder, custom iterators |
| **Special** | Static constructors, overflow checking, varargs (__arglist), nameof |

### Supported Syscalls

NeutrinoOS implements the Linux-compatible syscall interface inherited from ProtonOS.

| Category | Syscalls |
|----------|----------|
| **File I/O** | read, write, open, close, lseek, pread64, pwrite64, readv, writev |
| **File Metadata** | stat, lstat, fstat, access, chmod, fchmod, chown, fchown, lchown |
| **File System** | mkdir, rmdir, unlink, rename, link, symlink, readlink, creat, truncate, ftruncate |
| **Directories** | getdents64, getcwd, chdir, fchdir |
| **File Descriptors** | dup, dup2, dup3, fcntl, ioctl, pipe |
| **Memory** | mmap, mprotect, munmap, brk |
| **Process Info** | getpid, getppid, getuid, geteuid, getgid, getegid |
| **Process Control** | setuid, setgid, getpgid, setpgid, getsid, setsid, kill, exit |
| **Time** | clock_gettime, clock_getres, gettimeofday, nanosleep |
| **System** | uname, sysinfo, getrandom |
| **I/O Multiplexing** | poll |

## Building

NeutrinoOS uses ProtonOS's custom toolchain: a fork of bflat with fixes for our AOT scenarios.

### First-Time Setup

```bash
git submodule update --init --recursive
make install-deps  # Install system packages and .NET SDK 10 (requires sudo)
make deps          # Build runtime and bflat (~10-15 min first time)
```

The install script supports Ubuntu/Debian, Fedora, and Arch Linux. On Windows 11, run everything inside WSL2 Ubuntu 24.04 - see [docs/BUILD-WINDOWS.md](docs/BUILD-WINDOWS.md) for a step-by-step guide.

### Build and Run

```bash
make image      # Build kernel, assemblies and build/x64/neutrinoos.img
make run-qemu   # Minimal serial-only boot in QEMU (OVMF + neutrinoos.img)
make run        # Full QEMU test environment (adds test/sata disks)
make run-vbox   # Convert the image to .vdi and print the VirtualBox setup
./build.sh      # Clean build + test disks + IL disassembly
./kill.sh       # Kill running QEMU instances
```

### Serial console (Phase 2)

```bash
make run-qemu-serial       # Boot with the interactive console on the terminal
make run-qemu-serial-log   # Same, also tee output to build/x64/serial.log
make consoletest           # Build tests/ConsoleIoTest (console_io_test.dll)
```

From Windows 11, run the scripted acceptance (boots QEMU in WSL, drives the
console with keystrokes, asserts the log):

```powershell
scripts/test-console.ps1              # rebuild + full console acceptance
scripts/test-console.ps1 -SkipBuild   # reuse the current image
```

The interactive shell supports echo, backspace editing, Enter submit,
arrow-key history (32 entries), Ctrl+C (cancel line), Ctrl+D (logout) and
ANSI colors via `System.Console` in `korlib`. Boot markers on the image
root: `skip-boot-tests` (fast console cycles) and `run-console-test`
(run `console_io_test.dll` — the JIT-app console test, 46/46 passing via
the JIT→System.Console AOT bridge; see
[PHASE2-REPORT.md](PHASE2-REPORT.md) §5).

### Verifying a boot

`make run-qemu` attaches the serial console to your terminal. The boot log ends at the
Phase 1 console prompt (abridged):

```
NeutrinoOS v0.1 (x86-64 UEFI)
[BOOT] Loading kernel...
[BOOT] Relocating kernel...
...
  NeutrinoOS v0.1 (x86-64 UEFI)
[CONSOLE] Serial console initialized (ttyS0 @ 115200 8N1)
...
[SHELL] NeutrinoOS console ready.
neutrinoos>
```

Characters typed into the terminal are echoed back by the kernel. See
[docs/PHASE1-ACCEPTANCE.md](docs/PHASE1-ACCEPTANCE.md) for the full acceptance checklist,
[docs/PHASE2-DESIGN.md](docs/PHASE2-DESIGN.md) for the Phase 2 serial console design and
[docs/PHASE2-ACCEPTANCE.md](docs/PHASE2-ACCEPTANCE.md) for its acceptance guide.

### Toolchain

The kernel is compiled using a custom build of [bflat](https://flattened.net), a C# native AOT compiler:

- **[ProtonOS/runtime](https://github.com/ProtonOS/runtime)** - Fork of bflattened/runtime with fixes for array element type symbols in NativeAOT
- **[ProtonOS/bflat](https://github.com/ProtonOS/bflat)** - Fork of bflat configured to use locally-built ILCompiler from ProtonOS/runtime
- **NASM** - Assembles low-level x64 code (interrupts, context switch)
- **lld-link** - Links final UEFI PE executable
- **.NET SDK 10** - Builds driver and test assemblies as standard .NET DLLs for JIT loading

### Output

```
build/x64/
├── BOOTX64.EFI       # Kernel PE (installed as EFI/BOOT/KERNEL.BIN on the ESP)
├── LOADER.EFI        # UEFI bootloader (installed as EFI/BOOT/BOOTX64.EFI)
├── kernel_syms.elf   # GDB symbols for the AOT kernel
└── neutrinoos.img    # Bootable FAT32 disk image (ESP)
```

## Architecture

```
src/
├── korlib/              # Kernel runtime library (derived from bflat zerolib)
│   ├── Internal/        # Runtime internals, startup
│   └── System/          # Core types (Object, String, Array, Collections, etc.)
├── kernel/              # Kernel implementation
│   ├── Kernel.cs        # Entry point
│   ├── Exports/         # DDK and reflection exports for JIT code
│   ├── Memory/          # Heap, page allocator, GC, compaction
│   ├── PAL/             # Platform Abstraction Layer (Win32-style APIs)
│   ├── Platform/        # UEFI, ACPI, NUMA, CPU topology
│   ├── Runtime/         # PE loader, metadata reader, JIT compiler
│   ├── Threading/       # Scheduler, threads, per-CPU state
│   └── x64/             # x64-specific (GDT, IDT, APIC, SMP, assembly)
├── bootloader/          # UEFI bootloader (loads kernel at fixed address)
├── ddk/                 # Driver Development Kit (JIT-loaded)
│   ├── Kernel/          # Kernel API wrappers (Timer, Memory, Debug, etc.)
│   ├── Network/         # Network stack and management
│   │   ├── Stack/       # Protocol implementations (Ethernet, ARP, IP, TCP, UDP, DHCP, DNS)
│   │   ├── NetworkManager.cs    # Interface registration and naming
│   │   └── NetworkConfigParser.cs  # INI config file parser
│   └── Storage/         # Virtual filesystem abstraction
├── drivers/             # Device drivers (JIT-compiled)
│   └── shared/
│       ├── virtio/      # VirtIO common, virtio-blk, virtio-net
│       ├── storage/     # AHCI/SATA driver
│       └── filesystem/  # FAT32, EXT2 drivers
├── lib/                 # Application libraries
│   └── ProtonOS.Net/    # HTTP client library
├── AppTest/             # Application-level tests (HTTP, etc.)
├── TestSupport/         # Cross-assembly test helpers
└── JITTest/             # Comprehensive JIT test suite (2,983 tests)
    ├── Tests/IL/        # IL opcode tests
    ├── Tests/Features/  # JIT feature tests (generics, delegates, etc.)
    ├── Tests/Korlib/    # Runtime library tests
    └── Tests/Regression/# JIT regression tests

tools/
├── runtime/             # ProtonOS/runtime submodule (NativeAOT ILCompiler)
├── bflat/               # ProtonOS/bflat submodule (C# AOT compiler)
├── gdb-protonos.py      # GDB helper script for debugging
└── gen_elf_syms.py      # PDB to ELF symbol converter
```

## How It Works

NeutrinoOS uses ProtonOS's toolchain ([bflat](https://flattened.net) plus a custom NativeAOT runtime fork) to compile C# directly to a native UEFI executable with `--stdlib:none`. This means:

1. **No .NET runtime dependency** - The kernel IS the runtime
2. **Direct hardware access** - `unsafe` code and P/Invoke to assembly
3. **Full control** - Custom object layout, GC, exception handling

The kernel is AOT-compiled, but can load and JIT-compile standard .NET assemblies at runtime. This enables:
- Driver hot-loading without recompilation
- Test suites that run on the bare metal
- Future plugin/module support

The ~1750 lines of assembly (`native.asm`) handle only what C# cannot:
- Port I/O (`in`/`out` instructions)
- Control registers (CR0, CR3, CR4)
- Descriptor tables (GDT, IDT, TSS)
- Interrupt entry points
- Context switching

Everything else is C#.

## Documentation

- [Build Guide for Windows 11 + WSL2](docs/BUILD-WINDOWS.md) - Step-by-step build from Windows
- [Phase 1 Acceptance](docs/PHASE1-ACCEPTANCE.md) - Console-only acceptance criteria and how to verify them
- [Architecture Reference](docs/ARCHITECTURE.md) - System design and memory layout
- [Boot Protocol](docs/BOOT_PROTOCOL.md) - UEFI bootloader and kernel handoff
- [korlib Plan](docs/KORLIB_PLAN.md) - Runtime library roadmap
- [DDK Plan](docs/DDK_PLAN.md) - Driver development kit design
- [Phase 1 Report](PHASE1-REPORT.md) - What was removed, added, and deferred in the fork

## Contributing

This is currently a solo project. Pull requests are welcome and will be reviewed. Please open an issue first to discuss major changes.

## License

[AGPL-3.0](LICENSE) - Due to korlib's derivation from bflat's zerolib. NeutrinoOS is a fork of
[ProtonOS](https://github.com/ProtonOS/ProtonOS); the original license and copyright notices are
preserved - see [NOTICE](NOTICE). Modifications in this fork are distributed under AGPL-3.0.

This means:
- You can use, modify, and distribute freely
- Modifications must be shared under the same license
- Source must be provided if distributed or run as a network service

## Acknowledgments

- [bflat](https://github.com/bflattened/bflat) - The C# Native AOT compiler that makes this possible
- [bflattened/runtime](https://github.com/bflattened/runtime) - NativeAOT runtime fork that bflat is built on
- [.NET Runtime](https://github.com/dotnet/runtime) - Reference for runtime internals
