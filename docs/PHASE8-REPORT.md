# NeutrinoOS Phase 8 — Final Report

"ECOSYSTEM, DEVELOPER PLATFORM, AND MULTI-ARCHITECTURE"

Status: **complete** — all four capability areas delivered and
machine-verified. Details live in the per-area documents; the
step-by-step verification lives in `docs/PHASE8-ACCEPTANCE.md`.

## 1. Package manager (npkg)

Delivered: `.npkg` format (ZIP: `manifest.json` + `payload/` +
`checksums.sha256` + Ed25519 `signature.sig`), signed repository index,
transitive dependency resolver with `=`, `>=`, `>= <`, `~`, `^`
constraints, atomic install/remove/upgrade with a journal
(`/var/lib/npkg/journal.log`), trusted-key pinning
(`/etc/npkg/trusted-keys/`), repository priority ordering, and the
`install/remove/upgrade/list/search/info/verify/repo` command set on
device. Documented in `docs/PHASE8-NPKG.md`.

Verification (`build/p8-npkg-test.sh`, last run): **34/34 PASS** —
install utility/application/driver/chain, run through `/bin` wrappers,
dependency-guarded removal, `1.0.0 -> 1.1.0` upgrade, `checksums: OK`
verification, tampered-package rejection (`checksums: FAIL`), both
version-conflict paths (plan conflict, installed-version conflict),
missing-package reporting, clean removals, repository priority order.

Design notes worth keeping in mind:

- The guest resolver surfaces failures through `Resolver.LastError`
  instead of exceptions: exception unwinding across JIT-compiled frames
  is unreliable on the Tier-0 JIT (same constraint as
  `RepoClient.TryFetchIndex`).
- The guest npkg avoids `int.Parse` (the Tier-0 JIT cannot resolve it);
  all numeric parsing is manual.
- Assemblies are cached per boot by path: after `npkg upgrade`, a tool
  that already ran this boot keeps its previous image until reboot.

## 2. Driver framework and device model

Delivered: `NeutrinoOS.Driver.Abstractions.dll` ABI (semantic-versioned;
kernel rejects ABI-incompatible drivers), `IDevice`/`IDriver`/
`IDriverHost`/`IDeviceTree`/`IDriverServices`/`IDriverHostServices`,
kernel device tree + driver manager (Match -> Probe -> Start -> Stop),
PCI and VirtIO bus enumerators, packaged-driver loader
(`/var/lib/npkg/drivers/<name>/` manifest + payload through the AHCI
boot-volume bridge), and ports of all seven drivers (UART 16550, VGA
text, PS/2 keyboard, E1000, AHCI, VirtIO-Net, VirtIO-BLK). Documented
in `docs/PHASE8-DRIVER.md`.

**Hot-plug** (delivered in this phase): PCIe root ports are discovered
through a PCIe capability walk (bridge class, Root/Downstream Port,
Slot Implemented). Arrivals are detected by secondary-bus rescans from
the shell idle loop (200 ms cadence): a new function gets a PCI node +
VirtIO child, and the driver manager binds it (`[drv] bound ...`).
Departures complete through the port's Attention Button / Power
Indicator handshake (PCIe native path) or - on QEMU q35, where the ICH9
ACPI controller owns the root-port slots - through the
`acpi-pci-hotplug` IO registers (0xCC0: DOWN bits in, EJ acknowledge
out); the device then physically leaves the bus and the rescan unbinds
drivers child-first, marking tree nodes `DeviceStatus.Removed`.
Verified by `build/p8-hotplug-test.sh`: **9/9 PASS**, driver bound
334 ms after `device_add`, stopped 329 ms after `device_del`.

## 3. ARM64 (AArch64) port

Delivered: ARM64 kernel + single-stage UEFI bootloader
(`BOOTAA64.EFI`), VBAR_EL1 exception vectors, GICv2, generic timer,
PL011 UART console, MMU/paging with higher-half mapping, preemptive
scheduler with DAIF masking, PSCI-based SMP boot, and the VirtIO/PCI
paths needed by the QEMU `virt` machine. `make image ARCH=arm64` and
`make run-qemu-arm64` work from Windows 11 + WSL2; boot to the shell
measured at 6.0 s (vs 18.1 s for the x64 standard image, which runs the
x64-only boot suites). Documented in `docs/PHASE8-ARM64.md`.

## 4. SDK and ecosystem

Delivered: `NeutrinoOS.Sdk` archive (`dist/neutrinoos-sdk-1.0.0.tar.gz`)
with host tools (`npkg-host` including `pack`/`sign`/`publish`/
`repo-index`/`verify`, `npkg-repo-server`), libraries (Runtime/DDK,
Driver.Abstractions, Packaging), MSBuild props/targets that auto-package
on `dotnet build -c Release`, five `dotnet new` templates, five sample
projects, CI templates, and the SDK guides
(`docs/PHASE8-SDK.md`, `SDK-GETTING-STARTED.md`, `SDK-PACKAGING.md`,
`SDK-TESTING.md`, `SDK-CICD.md`, `SDK-API-REFERENCE.md`). Ecosystem
infrastructure: official signing key, package templates, community docs
(`COMMUNITY-GUIDE.md`, `PACKAGE-GUIDELINES.md`, `CODE_OF_CONDUCT.md`,
`CONTRIBUTING.md`), and the static site under `website/`.

Last recorded leg results (from `tests/run-phase8-tests.ps1` and the
individual scripts): npkg **34/34**, hot-plug **9/9**, ARM64 **6/6**,
SDK **15/15**, repository server **12/12**, samples **19/19**, archive
PASS, ecosystem **20/20**.

## 5. Benchmarks (spec, Task 6)

| Benchmark | Result | Conditions |
|-----------|--------|------------|
| `npkg install` with 10 dependencies | **38.2 s** | 11 packages; per package: fetch from local FAT repo, Ed25519 + SHA-256 verify, extract, journal (QEMU 8.2, 2 GB, 1 vCPU) |
| Driver load, hot-plugged VirtIO device | **334 ms** | monitor `device_add` -> `[drv] bound` (includes 200 ms poll) |
| Driver unload, hot-remove | **329 ms** | monitor `device_del` -> `[drv] stopped` (ACPI eject + rescan) |
| x64 boot to shell | **18.1 s** | standard `make image` (boot test suites enabled) |
| ARM64 boot to shell | **6.0 s** | same measurement method |

Re-run: `bash build/p8-t6-bench.sh` and `bash build/p8-hotplug-test.sh`.

## 6. Deviations and known limitations

1. **Driver hosts**: the spec calls for user-mode driver host processes;
   drivers currently run as managed in-kernel host contexts (the device
   model, ABI and lifecycle are host-agnostic so the move is
   incremental). This is the main deviation.
2. **Legacy transports**: after the framework port, the storage and
   network drivers' data paths still run through the legacy kernel/JIT
   transports while the framework owns lifecycle/logging.
3. **Repository fetch**: the guest npkg consumes local (`/repo`) and
   HTTP repositories; HTTPS repository fetching is exercised host-side
   only (the guest TLS stack exists but repo fetch stays in the clear,
   integrity coming from the signed index and packages).
4. **Hot-plug scope**: implemented on x64 (PCIe + ICH9 ACPI); ARM64
   compiles the device model and driver manager but not the hot-plug
   poll.
5. **Boot timeline on ARM64**: the `[Boot] t=...` timeline prints 0 ms
   on ARM64 (wall-clock boot is measured externally).
6. **Assembly loader cache**: assemblies load once per boot (keyed by
   path); upgraded files take effect on the next boot.
7. **Ring-3**: user mode (EL0) does not exist on ARM64 yet; the ring-3
   syscall tests are x64-only in this phase.

## 7. Where to look

- Verification steps, per criterion: `docs/PHASE8-ACCEPTANCE.md`
- Package manager: `docs/PHASE8-NPKG.md`
- Driver framework + hot-plug: `docs/PHASE8-DRIVER.md`
- ARM64: `docs/PHASE8-ARM64.md`
- SDK + workflow: `docs/PHASE8-SDK.md` (+ `SDK-*.md` guides)
- Ecosystem: `docs/PHASE8-ECOSYSTEM.md`, community docs at repo root
- Test runner: `tests/run-phase8-tests.ps1`
  (`npkg`, `drivers`, `hotplug`, `arm64`, `sdk`, `repo`, `samples`,
  `archive`, `ecosystem`)
