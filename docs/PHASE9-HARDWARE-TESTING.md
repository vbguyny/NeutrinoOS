# Physical Hardware Testing Procedure

This document describes how to validate NeutrinoOS on a physical
machine (Phase 9 Task 5/6). The automated harness covers QEMU and
VirtualBox; this procedure covers real UEFI hardware.

## 1. Prepare the build

On Windows (PowerShell) or Linux/WSL:

```powershell
# Linux/WSL
./build.sh                # produces build/x64/boot.img (and run.img harness copy)
```

Copy the built image to the machine that has the USB writer, or build
there directly.

## 2. Write the USB drive

Use the Phase 7 flasher (run as Administrator):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/flash-usb.ps1 -Disk 2
```

- `-Disk N` is the *physical* disk number of the USB stick (check with
  `Get-Disk`; **verify carefully** - the script overwrites the target).
- The script writes the raw image and optionally the test volumes.

## 3. Prepare the target machine

1. UEFI firmware settings:
   - **Secure Boot: disabled** (the loader is unsigned).
   - Boot order: USB first (or use the one-shot boot menu).
   - CSM/Legacy boot: disabled if the firmware offers UEFI-only.
2. Optional serial capture: connect a USB-serial adapter to the 16550
   header/com port if the board has one, 115200 8N1. On machines
   without a serial port, use the on-screen console or the VGA output
   (`build/gui-image.sh` GUI variant).
3. Connect the NIC (onboard + any add-in cards) and at least one
   storage device you can identify from its model string.

## 4. Boot and verify

| Step | Expectation |
|------|-------------|
| Firmware hands off to the NeutrinoOS loader | `[Boot]` timeline prints |
| Kernel reaches the shell | `neutrinoos>` prompt appears |
| NIC bound | serial log shows the PCI match + bind; `ifconfig` lists `eth0` |
| NVMe present | `[NVMe] model=... serial=... sectors=...` in the log |
| AHCI present | AHCI driver enumerates ports |
| USB hotplug | Plug a keyboard/flash drive; `[USB]` device lines appear |
| `poweroff` / `reboot` | Machine powers down / restarts cleanly |

## 5. What to collect for a bug report

- Full serial capture (or screen transcript from first `[Boot]` line).
- Exact hardware strings: motherboard model, BIOS version/date, CPU,
  NIC PCI ids (`lspci`-equivalent output is in the boot log), storage
  device model + firmware.
- The USB drive make/model and whether the failure is reproducible
  with a different drive.
- Whether the same image boots in QEMU/VirtualBox on another machine
  (isolates image vs hardware issues).

## 6. Known caveats

- Some firmware requires "USB legacy support" **off** when CSM is off.
- Certain NVMe drives need >2 s to become ready; the driver waits up
  to 3 s for CSTS.RDY (increase `ResetController` timeout if needed).
- e1000e / RTL8168 / i225 silicon is recognized and bound at the PCI
  framework level, but the register-level datapath for those families
  is not implemented yet - expect no traffic on those NICs (see
  `docs/HARDWARE-COMPATIBILITY.md`). Virtio-net and the software
  stack remain fully functional.

## 7. VirtualBox smoke test (no physical machine needed)

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-vbox.ps1   # automated acceptance VM
powershell -ExecutionPolicy Bypass -File scripts/gui-vm.ps1      # interactive GUI VM
```

These exercise the same CPU/storage path and `poweroff`/`reboot`
handling as a physical machine minus the NIC families above.
