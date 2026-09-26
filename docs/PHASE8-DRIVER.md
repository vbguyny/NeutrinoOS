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
| npkg driver packages: catalog, manifests, Create() factory, thunk adapter | done |
| Packaged drivers: device marshal + kernel services bridges (full lifecycle) | done |
| Port VirtIO-Net/Blk, E1000, AHCI onto the framework | done (legacy transports keep I/O) |
| Driver hosts as isolated user-mode processes | deferred (see Limitations) |
| PCIe hot-plug detection + driver load/unload | done (see Hot-plug below) |
| SDK: `templates/NeutrinoDriver` + `scripts/build-driver.ps1` | done |

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
  `Initialize(services) → Match(DeviceInfo) → Probe(DeviceInfo) → Start(DeviceInfo) → Stop(DeviceInfo)`.
  `Initialize` is called once by the driver's host (the driver manager, or a
  future separated host) to inject the `IDriverServices` instance.
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

## Built-in drivers follow-ups: the PCI and VirtIO ports

`src/kernel/Drivers/Builtin/E1000Driver.cs` — fourth port (PCI network):

- matches the Intel 8254x PCI ids (0x8086:0x10D3, plus the
  0x100E/0x100F/0x15A3 family), probes for the 128 KiB BAR0 register
  window, starts by mapping BAR0 through `IDriverServices.MapMmio` and
  logs the mapping. Packet I/O keeps running through the legacy kernel
  network path until the full port moves it behind the framework.

`src/kernel/Drivers/Builtin/AhciDriver.cs` — fifth port (storage):

- matches PCI SATA AHCI controllers (class 0x0106 masked over the prog-if),
  logs the framework handoff on start. The controller, ports and filesystem
  engine stay on the legacy AHCI path (the JIT'd `ProtonOS.Drivers.Ahci`
  assembly mounted the volumes before the framework starts); taking the
  ABAR behind the framework is the follow-up.

`src/kernel/Drivers/Builtin/VirtioNetDriver.cs` and
`VirtioBlkDriver.cs` — sixth and seventh ports (VirtIO children):

- match the `virtio` child nodes the enumerator adds (vendor 0x1AF4,
  device type flattened into `ClassCode`: 1 = net, 2 = blk); the start
  handlers log the handoff. The virtqueue transport keeps running through
  the legacy JIT'd VirtIO drivers until the full port. Verified on a probe
  VM with `virtio-blk-pci` + `virtio-net-pci` attached: `7 driver(s)
  registered, 7 device(s) started`, both child nodes bound.

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
- **Porting**: all seven drivers are on the framework (UART 16550, PS/2
  keyboard, VGA text, E1000, AHCI, VirtIO-Net, VirtIO-BlK). The storage and
  network ports currently assert framework ownership and logging while the
  data paths keep running through the legacy kernel/JIT transports; moving
  each transport behind `IDriverServices` is incremental follow-up work.
- **Loading from packages**: done. Driver packages in
  `/var/lib/npkg/drivers/<name>/` (manifest `driver{class,vendorIds,
  deviceIds,entryPoint}`) are discovered and loaded at boot, and the full
  packaged lifecycle (match, probe, start, services) is verified - see
  "Driver packages" above.
- **Hot-plug**: implemented - see "Hot-plug" below. PCIe root ports are
  discovered through a PCIe capability walk; arrivals are picked up by
  secondary-bus rescans; departures are completed through either the PCIe
  Attention Button / Power Indicator handshake or the ICH9 ACPI hotplug
  interface (whichever the machine routes unplug requests through).
- **USB**: out of scope for Phase 8 (deferred to Phase 9+).

## Hot-plug

The kernel monitors hot-plug slots from the shell idle loop
(`ShellMain.IdlePump -> PcieHotplug.Poll`, throttled to one pass per
~200 ms) and the driver manager loads/unloads drivers accordingly:

- **Port discovery** (`PcieHotplug.Initialize`): every PCI-to-PCI bridge
  (class 06/04) is walked for its PCI Express capability; ports whose PCIe
  capability type is Root Port or Downstream Port and that set the Slot
  Implemented bit get their secondary bus range remembered.
- **Arrival**: each poll rescans every remembered secondary bus. A
  function that answers config reads and is not in the device tree gets a
  PCI node (BARs sized, class mapped) plus a VirtIO child node when the
  vendor is 0x1AF4; `DriverManager.MatchAll` then runs the normal
  Match -> Probe -> Start lifecycle and logs `[drv] bound '<name>' to
  <path>`.
- **Departure**: the device no longer answering config reads is unbound -
  drivers are stopped child-first (`[drv] stopped '<name>' (<path>)`),
  registrations are released and the tree nodes are marked
  `DeviceStatus.Removed` so later match passes never rebind them.
- **Unplug requests**: on a PCIe-native path the port sets Attention
  Button Pressed (SLTSTA bit 0); the guest responds by powering the slot
  off through Slot Control (Power Indicator = off, Power Controller =
  power off), which performs the physical eject. On QEMU q35 the ICH9
  ACPI controller owns the root-port slots instead (QEMU rewrites the
  bus hotplug handler for cold-plugged bridges), so unplug requests
  arrive as DOWN bits in the `acpi-pci-hotplug` IO block (0xCC0): the
  poll completes them by writing EJ, and the removal is then seen by the
  bus rescan. `AcpiPciHotplug` implements this side; both paths converge
  on the same unbind logic.
- **Verification**: `build/p8-hotplug-test.sh` boots q35 with an empty
  `pcie-root-port` plus a monitor socket, hot-adds a virtio-net-pci
  (`device_add ... bus=hp0`), asserts the tree/bind evidence, then
  `device_del` and asserts the unbind evidence - 9/9 checks, driver
  bound ~334 ms after `device_add` and stopped ~329 ms after
  `device_del` (dominated by the 200 ms poll interval).
- ARM64 does not compile the hot-plug poll (x64 port I/O only); the
  device model and driver manager are shared.

## Driver packages

Driver packages installed by npkg are discovered and verified at boot by
`DriverPackageLoader` (called from `Kernel.Main` after `BindDrivers`):

- catalog: `/var/lib/npkg/installed.json`; per-package payload + manifest
  under `/var/lib/npkg/drivers/<name>/`, read through the AHCI boot-volume
  bridge (`FileExports.KernelBootRead`; the kernel VFS is not used because
  the acceptance images boot without a mounted root filesystem).
- a dedicated minimal JSON reader (`MiniJson`) parses catalog and
  manifests; the shared packaging parser cannot compile into the bflat AOT
  kernel (its `is bool` handling forces Boolean's MethodTable, whose
  `bool.ToString()` fails code generation), and the kind-tagged node design
  keeps every cast away from the IL scanner's TypeCast helpers.
- the entry assembly loads through `AssemblyLoader`; a static
  parameterless `Create()` factory (template convention) is JIT-compiled
  and invoked, and the factory result is validated (real runtime type with
  a non-empty interface map).
- references to `NeutrinoOS.Driver.Abstractions` resolve onto the kernel's
  compiled-in ABI copy (`AssemblyLoader.ResolveAssemblyRef`), so the
  `IDriver` MethodTable is shared between the AOT kernel and JIT-loaded
  drivers.

Binding goes through `PackagedDriverAdapter`: AOT code never calls an
interface method on the JIT object (AOT->JIT interface dispatch on
JIT-built MethodTables is not reliable here - an interface call on the
factory result landed on the wrong slot, observed returning another
driver's `Name`). Every `IDriver` member is invoked through a per-method
thunk: the driver method is JIT-compiled and called as an unmanaged
function pointer with the instance as the first argument (the same
direct-call pattern the kernel uses for AhciEntry helpers). Verified
working end-to-end at boot: catalog -> manifest -> payload -> assembly
load -> `Create()` factory -> `get_Name`/`get_Version`/`get_AbiMajor`/
`get_AbiMinor`/`Initialize` thunks -> ABI gate -> registration (the
`[drv] package '...' -> driver 'hello-driver' registered` line comes from
the driver's own `Name`).

The driver's `NeutrinoOS.Driver.Abstractions` references resolve to the
copy shipped on the image at `/lib/NeutrinoOS.Driver.Abstractions.dll`
(the packer's own assembly; the resolver lazy-loads it like any other
`/lib` assembly), and the driver's IL is JIT-compiled against it.

Device property reads across the JIT/AOT boundary are solved by the ABI's
`DeviceInfoMarshal` (`src/lib/NeutrinoOS.Driver.Abstractions/DeviceMarshal.cs`):
the kernel adapter builds a driver-world `DeviceInfo` *copy* for every
lifecycle call by invoking two marshal factories through JIT-compiled
thunks (`Create` + `WithMatch`; the ABI adds small-argument constructors
used by the copies, staying inside the Tier-0 JIT's `newobj` limit).
Every field read inside the driver therefore uses the driver's own layout;
kernel strings inside the copy stay safe (handing kernel strings to JIT'd
code is the proven direction). The earlier AOT-getter-bridge experiment
(`DriverAbiBridges`) is superseded and deleted.

Driver -> kernel *services* calls (`_services.Log(...)`) hit the same
cross-world wall: `KernelDriverServices` is AOT with no JIT-visible
metadata, so the interface dispatch landed on a zero target. The fix
follows the StringHelpers bridge pattern: `DriverServicesBridges`
registers a tiny AOT forwarder for each `IDriverServices` method keyed by
the *interface* full name, and `JitStubs.ResolveInterfaceMethodByName`
falls back to that registry (via `AotMethodRegistry.TryLookupEx`, matched
by method name and parameter count) when the metadata-based hierarchy
search finds nothing. Verified end-to-end at boot: `Match` -> marshal ->
bind, `Start` logs through the kernel services bridge, `[drv] 1 packaged
driver(s) loaded, 1 device(s) started`, zero faults.

The acceptance image pre-places a driver package (`preplaced.drvtest`,
built from the `tests/hello-driver` fixture) so this path runs on every
acceptance boot; the npkg acceptance itself runs on baseline images
(`P8_PREPLACE=off`) and the end-to-end flow (npkg install `tests.hello-driver`
in boot 1, loader in boot 2) is covered by `build/p8-drv-e2e.sh`.

## Driver SDK

- `templates/NeutrinoDriver/` — a ready-to-build driver project:
  `NeutrinoDriver.csproj` (references `NeutrinoOS.Driver.Abstractions` with
  `Private=false`: the ABI assembly is provided by the OS, never shipped in
  the package payload), `MyDriver.cs` (annotated `IDriver` skeleton),
  `manifest.json` (package + `driver` block) and a README with the build,
  pack and install steps.
- `scripts/build-driver.ps1` — Windows 11 (PowerShell 5.1) driver packager:
  builds the project, stages only the entry assembly, locates or builds the
  `npkg-host` CLI (`sdk/npkg`), then packs and signs
  (`npkg-host pack --manifest … --payload-dir … --out … --key …`); with
  `-RepoDir` it also copies the `.npkg` into a repository and refreshes the
  signed index. Verified end-to-end (build → pack → `npkg-host verify`
  PASS) against the template with the test key.
