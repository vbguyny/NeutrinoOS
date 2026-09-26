# Phase 9 Task 1 — USB Stack

NeutrinoOS ships an in-kernel USB stack: an xHCI (USB 3.x) host
controller driver, USB device enumeration, and class drivers for
HID keyboards/mice, mass storage (BOT/SCSI) and CDC-ACM serial
adapters, with polled hot-plug. Everything is C# (kernel AOT) and
tested end-to-end in QEMU.

## Architecture

| Component | Files | Status |
| --- | --- | --- |
| xHCI host controller | `src/kernel/Usb/Xhci/XhciController.cs`, `XhciTypes.cs` | Working: init, rings, slots, ports, control/bulk/interrupt transfers |
| USB core (enumeration) | `src/kernel/Usb/UsbCore.cs` | Working: port reset, slot addressing, descriptor reads, SET_CONFIGURATION, class dispatch |
| USB descriptors | `src/kernel/Usb/UsbDescriptors.cs` | Device/config/interface/endpoint/HID/CDC decoders (flat endpoint storage) |
| HID (keyboard/mouse) | `src/kernel/Usb/UsbHid.cs` | Boot-protocol keyboard feeds the CAL; mouse movement tracked, cursor routing stubbed |
| Mass storage (BOT) | `src/kernel/Usb/UsbStorage.cs` | INQUIRY / READ CAPACITY / READ10 / WRITE10; disks named `sda`, `sda1`… |
| CDC-ACM serial | `src/kernel/Usb/UsbSerial.cs` | Line coding + control line state; polled RX ring; ports named `ttyUSB0`… |
| Driver binding | `src/kernel/Drivers/Builtin/XhciDriver.cs` | Phase 8 `IDriver`; matches PCI class `0x0C0330` |
| Hot-plug | `UsbCore.Poll()` via `ShellMain.IdlePump` | PORTSC connect-mask XOR per idle tick; ~instant in the shell |
| Shell command | `usb` builtin (`ShellBuiltins.cs`) | Lists controller, devices, disks, serial ports |

## Design notes

- **Polled controller.** Interrupts are routed through the CAL already,
  but MSI is not plumbed for drivers; the stack polls instead. The
  event ring is drained during every transfer wait (`WaitUntil` →
  `PollEvents`) and from the shell idle pump. IMAN.IE stays 0.
- **Ring model.** One 64-TRB command ring, one 256-TRB event ring
  (ERST with 1 segment), one transfer ring per endpoint (DCI-indexed,
  linked with a cycle-toggling Link TRB). Physical addresses come from
  `UsbDma` (PageAllocator pages viewed through the physmap).
- **Enumeration.** Port reset → `Enable Slot` → `Address Device`
  (EP0 at default MPS) → descriptor-8 → `Evaluate Context` (real MPS)
  → descriptor-18 → configuration → `SET_CONFIGURATION` +
  `Configure Endpoints` → class bind.
- **Hot-plug.** `UsbStack.Poll()` compares a CCS bitmask sampled from
  all PORTSC registers with the previous mask; new bits enumerate,
  cleared bits unbind/disable. The IdlePump runs it between shell
  commands, so plug/unplug is picked up in a fraction of a second.
- **Timezone-free timeouts.** `WaitUntil` uses the HPET (falling back
  to APIC ticks) with an iteration backstop. The APIC tick counter is
  not running yet while boot-time drivers start, so tick-based
  deadlines could never fire there.
- **MMIO access carries memory barriers.** This code generator has
  been observed to CSE consecutive loads through the same base pointer
  into a single load. Every MMIO read accessor in `XhciController`
  ends with `CPU.MemoryBarrier()` so each read is emitted.

## xHCI facts pinned down during bring-up (QEMU)

These were all verified against QEMU's `hcd-xhci.c` and live hardware
state; they are easy to get wrong from the spec alone:

- Interrupter 0's register set lives at **RTSOFF + 0x20** (not
  RTSOFF + 0x00); the first 0x20 bytes are MFINDEX/reserved. Writes
  below +0x20 are silently ignored, which looks exactly like "events
  never arrive".
- The **ERST entry** is `{u64 base; u32 size; u32 rsvd;}` — segment
  size is a plain dword at offset +8 (TRB count 16..4096).
- **Slot context dword 1 bits 23:16 = Root Hub Port Number** (not a
  later dword). QEMU returns TRB error (5) if the looked-up port does
  not exist.
- **Endpoint context**: d1 bits 31:16 = MaxPacketSize; d2/d3 = TR
  dequeue pointer (bit 0 = DCS); d4 = average TRB length. Interval
  goes in d0 bits 23:16.
- **BOT**: the CSW signature is `0x53425355` ("USBS"), bytes
  `55 53 42 53` — the CBW one (`USBC`) shares the first three bytes.
- QEMU's `nec-usb-xhci` exposes 4 USB3 + 4 USB2 ports at register
  strides 0x10 from 0x400; USB3 port `i` and USB2 port `i+4` share a
  bus port. Port numbers in slot contexts are 1-based.
- A 64-bit BAR assigned above 4 GB (OVMF put `qemu-xhci` BAR0 at
  `0xC000000000`) must be mapped through the physmap window; the
  `KernelDriverServices.MapMmio` path maps MMIO via
  `0xFFFF800000000000 + phys` large pages.

## Testing

```text
make run-qemu-usb          # interactive QEMU with xHCI + keyboard + stick
bash build/p9-usb-test.sh  # automated acceptance (11 checks)
```

The acceptance test boots with `-device qemu-xhci`, a `usb-kbd` and a
`usb-storage` stick and asserts, from the serial transcript:

1. boot to the shell prompt
2. xHCI controller started
3. HID keyboard enumerated and bound
4. mass storage enumerated (BOT)
5. `usb` reports the controller
6. `usb` lists devices
7. `usb` lists the disk
8. disk reports a sector count (READ CAPACITY)
9. typing `version` through USB HID (QEMU sendkey) runs the shell
   command end-to-end
10. hot-plug: `device_add usb-kbd` enumerates a second keyboard
11. hot-unplug: `device_del` unbinds the driver and disables the slot

Current result: **11 PASS / 0 FAIL (ALL-PASS)**.

## Deferred / follow-ups

- **EHCI, UHCI, OHCI** are documented as deferred (no QEMU system
  needs them; xHCI covers the test matrix and modern hardware).
- **USB hubs** are detected and bound but downstream port management
  is not implemented; devices behind hubs are documented as a
  follow-up.
- **MSI/MSI-X** for the event ring (currently polled end-to-end).
- **Isochronous** transfers (audio/video) are not implemented.
- **Mouse cursor routing**: movement is tracked; wiring it into the
  framebuffer cursor is queued with the GUI work.
- **VFS nodes**: disks/serial ports are named `sda`/`ttyUSB0` and
  exposed through the `usb` shell command; creating real `/dev/sda`
  block nodes + mountable FAT32/EXT2 on USB media is tracked with the
  VFS unification work (the in-kernel FAT path currently serves the
  internal disk).
- **VirtualBox / real hardware**: the QEMU xHCI path is verified;
  physical-machine validation is part of the Phase 9 hardware
  checklist.
