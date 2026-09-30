# NeutrinoOS Hardware Compatibility List (HCL)

Phase 9 hardware enablement status. "Verified" means exercised on a
real or emulated device in this repository's test harness; "implemented"
means driver code exists but has only been compile/link-checked or
partially exercised; "detected" means the device is recognized and
reported but has no datapath yet.

## CPUs / platforms

| Platform | Status | Notes |
|----------|--------|-------|
| x86_64 UEFI (QEMU q35, OVMF) | Verified | Primary development target |
| x86_64 UEFI (VirtualBox 7) | Verified | `scripts/test-vbox.ps1`, `scripts/cli-vm.ps1` |
| x86_64 multi-socket NUMA (QEMU, SMP) | Verified | Phase 7 SMP fix |
| ARM64 (QEMU virt) | Verified | Boot + console |
| Real x86_64 motherboards | Procedure ready | See `docs/PHASE9-HARDWARE-TESTING.md`; flashing via `scripts/flash-usb.ps1` |

## Network controllers

| Device | PCI IDs | Status | Notes |
|--------|---------|--------|-------|
| Virtio-net | 1AF4:1000 | Verified | Full datapath; carries host-facing tests |
| Intel e1000 (82540EM etc.) | 8086:100E/100F/10D3/15A3 | Detected | Kernel framework driver binds BAR0; datapath uses virtio in tests |
| Intel e1000e (82574L, I217/I218/I219) | 8086:10D3, 15B7-15F9 range | Detected | Same framework binding; real-hardware datapath pending |
| Intel i225/i226 (2.5GbE) | 8086:15F2/15F3/125B-125D | Detected | Device recognized and logged; igc datapath not yet implemented |
| Realtek RTL8168/8111 | 10EC:8168/8161/8167 | Detected | Same as above (register-level driver path stubbed) |
| Broadcom NetXtreme | 14E4:* | Not supported | Detected on real hardware; requires bnxt/NetXtreme driver |

## Storage controllers

| Device | Status | Notes |
|--------|--------|-------|
| AHCI/SATA (ICH9, Q35) | Verified | Root filesystem; boot volume mounting; full read/write + FAT/ext2 |
| **NVMe (PCIe SSD)** | **Verified** | `ProtonOS.Drivers.Nvme`: admin + I/O queues, IDENTIFY, READ/WRITE/FLUSH; QEMU `-device nvme` acceptance (`build/p9-nvme-test.sh`) |
| Virtio-blk | Verified | Test harness image |
| USB Mass Storage (BOT) | Verified | Phase 9 Task 1; USB flash drives appear as block devices |
| Real SATA/NVMe SSDs | Procedure ready | Same driver path as QEMU equivalents |

## USB

| Device | Status | Notes |
|--------|--------|-------|
| xHCI controller (NEC/Renesas/Intel, QEMU `qemu-xhci`) | Verified | Task 1 |
| HID keyboard (boot protocol) | Verified | Hot-plug |
| Mass storage (BOT, SCSI transparent) | Verified | Read sectors from flash drive |
| CDC-ACM serial adapters | Verified | Appears as `com1`-style USB serial |

## Serial

| Device | Status | Notes |
|--------|--------|-------|
| 16550 UART | Verified | Primary serial console |
| PL011 (ARM64) | Verified | ARM64 console |
| USB CDC-ACM | Verified | Task 1 |

## Example physical test bed (used for procedures)

| Component | Model |
|-----------|-------|
| System | Intel NUC 11 (or similar x86_64 UEFI) |
| CPU | Intel i7-1165G7 |
| NIC | Intel i225-V |
| Storage | Samsung 980 Pro NVMe (or any SATA SSD) |
| USB | Generic USB 2.0/3.0 flash drive |
| Firmware | UEFI, Secure Boot **off** |

## Legend

- **Verified** – exercised in this repository's automated or
  documented manual tests.
- **Detected** – the device is recognized (PCI probe + framework bind +
  log output) but the full driver datapath is not implemented yet.
- **Not supported** – no code path.

Contribution guidance: new NICs should implement the Phase 8 `IDriver`
interface, bind in `Bind*Driver()` in `src/kernel/Kernel.cs` alongside
the existing probes, and register with the network manager using the
`virtio-net` entry as the template.
