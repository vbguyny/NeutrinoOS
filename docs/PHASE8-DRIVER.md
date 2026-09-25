# Phase 8 — Driver Framework

## Overview

NeutrinoOS uses a structured driver model in the style of the Fuchsia driver
framework: a kernel-maintained **device tree** populated by **bus
enumerators**, an ABI-gated **driver registry**, and drivers that reach
hardware only through **kernel services**. The framework is defined by the
`NeutrinoOS.Driver.Abstractions` assembly (`src/lib/NeutrinoOS.Driver.Abstractions`),
and implemented by the kernel (`src/kernel/Drivers`).

Implementation staging (this phase):

| Piece | Status |
|---|---|
| Device model (`DeviceInfo`, resources, enums) | done |
| ABI assembly + versioning (`DriverAbi`, major/minor) | done |
| PCI enumerator (BAR sizing, IRQ, class mapping) | done |
| VirtIO sub-enumerator (vendor 0x1AF4, legacy + modern ids) | done |
| Platform devices (UART 16550, PS/2 8042) | done |
| Device tree + `IDeviceTree` | done |
| Driver manager: Match → Probe → Start, ABI gate | done |
| `IDriverServices` implementation (MMIO/DMA/IRQ/devnodes/log) | done |
| Driver ports: UART 16550, PS/2 keyboard, VGA text (PCI) | done |
| Port VirtIO-Net/Blk, E1000, AHCI onto the framework | incremental |
| Driver hosts as isolated user-mode processes | deferred (see Limitations) |
| PCIe hot-plug detection | deferred (see Limitations) |
| SDK template + `scripts/build-driver.ps1` | deferred (see Limitations) |

## Device model

`DeviceInfo` describes a device instance: id, parent id, tree path
(e.g. `/pci/00:02.0`, `/pci/00:03.0/virtio-net`), bus + bus address, PCI-style
vendor/device ids, a `DeviceClass`, a raw class code, and a resource array:

- `DeviceResourceKind.IoPort` — `Base` + `Length` (I/O port range)
- `DeviceResourceKind.Mmio` — `Base` + `Length` (physical MMIO window)
- `DeviceResourceKind.Irq` — `Base` = legacy IRQ line
- `DeviceResourceKind.Dma` — `Length` = max transfer size hint

`sanitize`-free: a driver receives the node for the device it is bound to,
plus `IDeviceTree` for walking the rest.

## The ABI

`NeutrinoOS.Driver.Abstractions` (compiled both into the kernel and available
to driver projects) contains:

- `DriverAbi` — `Major = 1`, `Minor = 0`, `IsCompatible(major, minor)`.
  Drivers whose major differs, or whose minor is newer, are refused by the
  driver manager.
- `IDriver` — `Name`, `Version`, `AbiMajor/AbiMinor`, lifecycle
  `Match(DeviceInfo) → Probe(DeviceInfo) → Start(DeviceInfo) → Stop(DeviceInfo)`.
- `IDriverServices` — the only hardware/kernel authority a driver has:
  `MapMmio`, `UnmapMmio`, `AllocateDma`, `FreeDma`,
  `RegisterInterrupt(irq, callback)`, `UnregisterInterrupt`,
  `CreateDeviceNode`, `RemoveDeviceNode`, `Log`.
- `IDriverHost` / `IDriverHostServices` — host-side contract
  (`Initialize`, `GetDrivers`, `Shutdown`; `OnDeviceAdded`,
  `OnDeviceRemoved`, `OnInterrupt`) used when drivers move to separated
  hosts.
- `IDeviceTree` — `Root`, `Count`, `GetAt`, `GetById`, `GetByPath`,
  `GetChildren`.

Drivers are written against the abstraction types. The kernel build compiles
the same sources (`Makefile`: `DRIVER_ABI_SRC`), so the JIT resolves the
driver's type references to the kernel's AOT copies — the same mechanism the
DDK uses today.

## Kernel implementation

`src/kernel/Drivers`:

- `DriverFramework.Initialize()` (called from `Kernel.Main` right after PCI
  enumeration): enumerates PCI, then VirtIO, then adds platform devices,
  logs the tree (`[DeviceTree] …`), registers built-in drivers, and runs the
  first match pass (`[drv] …`).
- `KernelDeviceTree` — fixed-capacity tree (128 nodes), implements
  `IDeviceTree`.
- `PciBusEnumerator` — walks the PCI layer that runs earlier; sizes BARs
  with the standard write-all-ones/restore probe (32-bit and 64-bit memory
  BARs, I/O BARs), reads the IRQ line (config 0x3C), maps PCI
  base/subclass to `DeviceClass`.
- `VirtioBusEnumerator` — adds a child node per VirtIO function
  (`virtio-net`, `virtio-blk`, `virtio-console`, …) under its PCI parent.
- `DriverManager` — registry (32 entries), ABI gate, `MatchAll()` with
  first-match-wins per device, `StopAll()`; logs `[drv] bound 'name' to path`.
- `KernelDriverServices` — identity-mapped MMIO, heap-backed DMA (16-byte
  aligned by the allocator, larger alignments over-allocated and tracked),
  IRQ registration on vectors 32+n with a shared thunk that forwards to the
  driver callback and sends the APIC EOI, console logging. Device-node
  creation currently returns the canonical `/dev/<name>` path (VFS wiring
  pending; no driver needs it yet).

## Built-in drivers

`src/kernel/Drivers/Builtin/Uart16550Driver.cs` — first port:

- matches the platform `uart0` node (vid 0xFFFF, did 0x1650),
- probes for the I/O port resource,
- starts by registering IRQ 4 through `IDriverServices` (this replaces the
  legacy console registration on the same vector; the legacy registration
  still covers the window before the framework initializes),
- the interrupt body stays in `Uart16550.HandleInterruptBody()` shared with
  the boot console; the services thunk owns the EOI.

`src/kernel/Drivers/Builtin/Ps2KeyboardDriver.cs` — second port:

- matches the platform `ps2` node (vid 0xFFFF, did 0x8042),
- probes for the 8042 I/O port range (0x60 + 4),
- starts by registering IRQ 1 through `IDriverServices`; the scancode
  drain lives in `Ps2Keyboard.HandleInterruptBody()` (EOI by the thunk).
  The 8042 controller configuration itself stays in
  `Ps2Keyboard.Initialize` (the console layer needs it before the
  framework exists).

`src/kernel/Drivers/Builtin/VgaTextConsoleDriver.cs` — third port, and the
first PCI-matched driver:

- matches PCI VGA-compatible display controllers (class code 0x030000),
- probes for the framebuffer MMIO BAR,
- starts by mapping the BAR through `IDriverServices.MapMmio` (identity
  map) and logs the mapping. NOTE: the text console still renders via the
  legacy identity-mapped text window at 0xB8000; moving the console to a
  linear-mode mapped framebuffer is a follow-up. The driver provides
  framework ownership of the display device and verifies the PCI MMIO
  mapping path.

## Debugging notes (bflat constraints hit while building this)

- `bool[]` arrays make bflat fail with "Code generation failed for method
  'bool.ToString()'". Use `byte[]` instead (see `KernelDriverServices`).
- Arrays of pointer types (`void*[]`) fail IL scanning with "Unable to cast
  object of type 'Internal.TypeSystem.PointerType' to type
  'Internal.TypeSystem.DefType'". Store `ulong` values instead.
- Inside `ProtonOS.Drivers`, a bare `Arch` binds to the `ProtonOS.Arch`
  namespace — always write `ProtonOS.X64.Arch` for the CPU class.

## Limitations / deviations (documented honestly)

- **Driver hosts**: the spec calls for drivers in user-mode driver host
  processes. The machine has ring-3 machinery (unused by the shell), but the
  current port runs drivers as managed in-kernel host contexts. Drivers only
  touch hardware through `IDriverServices`, so moving them behind a
  transport into isolated hosts later does not change driver code. This is
  the main deviation from the Fuchsia model and is expected to be revisited
  in a later phase.
- **Porting**: only UART 16550 is on the framework so far. The other drivers
  (PS/2, VGA text, VirtIO-Net/Blk, E1000, AHCI) still run through their
  legacy kernel paths; they port incrementally (each port keeps the legacy
  path until verified).
- **Loading from packages**: drivers in `/var/lib/npkg/drivers/` (npkg driver
  packages carry `driver{class,vendorIds,deviceIds,entryPoint}` manifest
  metadata) are not auto-loaded yet; the loader integration is the next
  increment after the remaining ports.
- **Hot-plug**: PCIe hot-plug detection (`Attention Button`/`Power
  Indicator` and the PCIe capability walk) is not implemented yet.
- **USB**: out of scope for Phase 8 (deferred to Phase 9+).
- **SDK/template/build-driver.ps1**: pending; the ABI assembly is already
  packaged for reuse (`NeutrinoOS.Driver.Abstractions.csproj`).
