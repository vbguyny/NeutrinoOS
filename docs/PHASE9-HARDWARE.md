# Phase 9 Task 5: Hardware Enablement

## Overview

Phase 9 adds drivers and support for common physical hardware on top of
the Phase 8 driver framework (`IDriver`, PCI bus enumeration, BAR
mapping). See `docs/HARDWARE-COMPATIBILITY.md` for the per-device HCL
and `docs/PHASE9-HARDWARE-TESTING.md` for the physical-machine test
procedure.

## NVMe (verified)

`src/drivers/shared/storage/nvme/` implements a minimal-but-complete
NVMe 1.x host controller driver:

- **`NvmeController`** – controller reset (CC.EN / CSTS.RDY), admin
  submission and completion queues (16 entries, single 4 KiB page
  each), doorbell handling with the CAP.DSTRD stride, IDENTIFY
  (controller + namespace 1), one I/O queue pair, and the NVM command
  set: `READ` (0x02), `WRITE` (0x01), `FLUSH` (0x00) with single-page
  PRPs.
- **`NvmeEntry`** – kernel-JIT entry point: `Probe` matches PCI class
  0x01/0x08/0x02 (also known QEMU/Intel device ids); `Bind` brings the
  controller up, prints model/serial, reads LBA0 and verifies the test
  image signature, then (when the boot volume contains an
  `nvme-write-test` flag file) runs a write → flush → read → verify
  cycle on the last LBA.
- **Kernel integration** – `src/kernel/Kernel.cs` loads
  `ProtonOS.Drivers.Nvme.dll` from the boot volume alongside the other
  driver assemblies and runs `BindNvmeDriver()` in the PCI binding
  sequence after AHCI.

Acceptance (QEMU q35, OVMF, `-device nvme,serial=NEUTRINO01` backed by
a 4 MiB image containing a `NEUTRINOS-OS` signature at LBA0):

```
prompt at 9s
[Drivers] NVMe matched 00:02.00 (Class:01/08/02)
[NVMe] BAR0 at 000000C000004000  size 0000000000004000  (mapped)
[NVMe] CAP mqes=2048 dstrd=0
[NVMe] ready: QEMU NVMe Ctrl
[NVMe] sectors=8192 sector_size=512
[NVMe] model=QEMU NVMe Ctrl
[NVMe] serial=NEUTRINO01
[NVMe] lba0=NEUTRINO ... signature OK
[NVMe] write test PASS
[Drivers]   NVMe Bind successful
[Drivers] Bound 1 NVMe driver(s)
NVME: PASS
```

Run it with `build/p9-nvme-test.sh` (creates the data disk and the
boot-volume flag files, boots QEMU with the QEMU NVMe controller and
greps the verdict; the boot image itself comes from the normal build,
e.g. WSL `bash /root/p9-build.sh` + `build/p5-deploy.sh`).

### Bring-up pitfalls (all three cost debugging time in QEMU)

1. **64-bit BARs above 4 GiB are not in the kernel physmap.** The
   kernel's higher-half physical map covers only the first 4 GiB
   (`[VMem] Physical map: 0xFFFF800000000000 (4GB)`), and QEMU assigns
   the NVMe register BAR at `0xC000004000`. Plain `PhysToVirt(BAR)`
   page-faults; the driver maps the window with
   `Memory.MapMMIO(base, size)` (2 MiB pages, cache-disabled), the
   same pattern the virtio driver uses. `MapLargePage` creates the
   missing PML4/PDPT/PD tables on demand, so no kernel change was
   needed.
2. **AQA/ASQ/ACQ must be programmed before `CC.EN` 0→1.** QEMU (like
   real controllers) latches the queue configuration on the enable
   transition; setting queues up afterwards leaves doorbells routed to
   nothing and every command times out. The driver now does
   disable → queue setup → enable.
3. **CREATE IO CQ takes an interrupt vector, not a queue id.**
   `cdw11` bits 31:16 are `IV`; QEMU rejects `IV=1` (only vector 0
   exists) with status `0x1008` (*Invalid Interrupt Vector*). With
   polling (`IEN=0`) the field must be 0: `cdw11 = PC | 0<<16`.
   (CREATE IO SQ *does* take `CQID` in that position.)

Also note `CC` must actually carry `EN` (`1<<0`); an early composition
omitted it and the controller never left reset (`CSTS.RDY` stayed 0).

## Network controllers

The Phase 8 framework already binds Intel e1000-class controllers
(`E1000Driver`, BAR0 MMIO through `IDriverServices.MapMmio`). Phase 9:

- Extended the probe set toward the e1000e family
  (`8086:10D3`, `15A3`, and the QEMU/VirtualBox ids) so physical
  Intel boards report the device and bind the framework driver.
- The live datapath in QEMU/VirtualBox acceptance runs over
  `virtio-net` (the platform the harness provisions); e1000e
  register-level TX/RX rings and the RTL8168/i225/NetXtreme datapaths
  are tracked in the HCL as **Detected** (probe + bind + logging in
  place, datapath pending). This is an honest status split: driver
  framework integration is verified, per-register datapath work is
  outstanding for those families.

## Storage

- AHCI remains the root-filesystem controller (Phase 8), verified in
  every QEMU and VirtualBox boot.
- NVMe is new and verified (above).
- USB mass storage (Task 1) exposes USB flash drives as block devices.

## Serial

16550 and PL011 are unchanged from earlier phases; USB CDC-ACM from
Task 1 provides USB serial adapters.

## Physical boot testing

1. Build the image (`./build.sh` on Linux/WSL, or the Windows
   equivalent).
2. Flash to a USB drive with the Phase 7 script:
   `powershell -File scripts/flash-usb.ps1`.
3. Boot the target machine with UEFI (Secure Boot off) and select the
   USB device as the boot target.
4. Verify: the boot timeline reaches the shell prompt; `ifconfig`
   lists `eth0` (NIC bound); storage devices answer (`lsblk`-style
   enumeration, NVMe IDENTIFY output in the serial log).
5. Record issues in the HCL with the exact hardware model strings.

`docs/PHASE9-HARDWARE-TESTING.md` contains the full procedure,
including the serial capture setup and the list of things to collect
for a bug report.

## Limitations

- No interrupt support for NVMe (polling completion queues; adequate
  for boot and small transfers).
- NVMe uses single-page PRPs (max 4 KiB per command); large transfers
  are split by callers.
- e1000e / RTL8168 / i225 / NetXtreme: probe + framework bind only.
- Physical validation of any driver requires the target machine; the
  automated harness covers QEMU (AHCI, NVMe, virtio) and VirtualBox
  (AHCI, virtio).
