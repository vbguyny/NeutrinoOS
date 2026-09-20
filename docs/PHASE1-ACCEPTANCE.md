# Phase 1 Acceptance Checklist - NeutrinoOS ("Fork & Strip")

This document lists the Phase 1 acceptance criteria and the exact procedure to
verify each one. All commands are run inside WSL2 Ubuntu 24.04 (see
[BUILD-WINDOWS.md](BUILD-WINDOWS.md) for setup), from the repository root.

> **Verification status (recorded in this fork):** items 1, 2, 4, 5, 7 and 8 are
> verified - see [PHASE1-REPORT.md](../PHASE1-REPORT.md) section 4. The banner half
> of item 3 passes, but the system currently halts during driver JIT with a
> **pre-existing upstream General Protection Fault** that reproduces identically on
> unmodified ProtonOS with both the cached and the project's custom ILCompiler, so
> items 3/6 (prompt + echo) remain blocked on an upstream fix.

---

## Checklist

| # | Criterion | Verified by |
| - | --- | --- |
| 1 | The repository is named NeutrinoOS and preserves AGPL-3.0 | Inspection (below) |
| 2 | `make image` completes without errors in WSL2 Ubuntu 24.04 | Command (below) |
| 3 | `make run-qemu` boots the image under OVMF UEFI and prints the NeutrinoOS banner to the serial console | Command + transcript |
| 4 | No GOP, framebuffer, or graphics initialization occurs during boot | Code inspection (greps below) |
| 5 | All boot and kernel log output appears on the serial console | Transcript + code inspection |
| 6 | The system reaches a `neutrinoos>` prompt and echoes typed characters | Transcript / piped-input test |
| 7 | No C or C++ files exist in the kernel or bootloader directories | File scan (below) |
| 8 | `docs/BUILD-WINDOWS.md` allows a fresh Windows 11 + WSL2 machine to build and run by following only the documented steps | Follow the guide on a clean machine |

---

## 1. Repository identity and license

```bash
head -20 README.md       # first line: "# NeutrinoOS"; documents fork origin + Phase 1 scope
head -10 NOTICE          # fork origin, upstream URL, baseline commit, license note
head -5 LICENSE          # GNU AFFERO GENERAL PUBLIC LICENSE Version 3
```

The `LICENSE` file is the unmodified upstream AGPL-3.0 text; `NOTICE` records the
fork origin. User-visible banners/strings in the running system report
"NeutrinoOS" (see items 3 and 6).

## 2. `make image` succeeds (WSL2 Ubuntu 24.04)

```bash
git submodule update --init --recursive
make install-deps   # system packages + .NET SDK 10 (needs sudo)
make deps           # build the custom runtime + bflat fork (~10-15 min first time)
make image
```

Expected: no errors, ending with the FAT image directory listing. Artifacts:

```
build/x64/BOOTX64.EFI      # kernel PE
build/x64/LOADER.EFI       # UEFI bootloader
build/x64/neutrinoos.img   # 64 MB FAT32 boot image
```

Quick sanity check of the image contents:

```bash
mdir -i build/x64/neutrinoos.img ::/EFI/BOOT/
#  BOOTX64.EFI   <- bootloader
#  KERNEL.BIN    <- kernel
```

## 3. Boot under OVMF with serial banner

```bash
make run-qemu
```

`tools/run-qemu.sh` runs QEMU with `-machine q35 -m 2G`, OVMF via pflash,
`neutrinoos.img` on virtio, `-display none -serial stdio` (no graphics device
is used; GOP is never initialized).

Expected output (abridged - the kernel also runs its on-boot test suites):

```
NeutrinoOS v0.1 (x86-64 UEFI)
[BOOT] Loading kernel...
[BOOT] Relocating kernel...
[BOOT] Copying files...
[BOOT] Exiting boot services...
  NeutrinoOS v0.1 (x86-64 UEFI)
[CONSOLE] Serial console initialized (ttyS0 @ 115200 8N1)
...
[SHELL] NeutrinoOS console ready.
neutrinoos>
```

Exit QEMU with `Ctrl+A`, then `X`.

To capture a machine-readable transcript:

```bash
printf '' | timeout 120 qemu-system-x86_64 \
    -machine q35 -m 2G \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,file=build/x64/OVMF_VARS.fd \
    -drive file=build/x64/neutrinoos.img,format=raw,if=virtio \
    -display none -serial file:serial.log -no-reboot -no-shutdown || true
grep -a "NeutrinoOS v0.1 (x86-64 UEFI)" serial.log   # banner present
grep -a "\[BOOT\] Loading kernel" serial.log          # bootloader on serial
```

## 4. No GOP / framebuffer / graphics initialization

Code inspection (from repository root):

```bash
# 4a. No UEFI graphics protocol usage anywhere in src/
grep -rniE "EFI_GRAPHICS_OUTPUT|FrameBufferBase|FrameBufferSize|LocateProtocol|OpenProtocol" src/
#    -> no output

# 4b. No framebuffer/graphics code paths (no VGA writes, no blitting, no display manager)
grep -rniE "0xB8000|blit|SetPixel|DisplayManager|IDisplayDevice|IFramebuffer" src/
#    -> no output

# 4c. Remaining "framebuffer" mentions are comments and reserved ABI fields only
grep -rni "framebuffer" src/
#    -> comments in src/bootloader/boot.asm, src/kernel/Platform/DebugConsole.cs,
#       and the reserved (never populated) BootInfo struct fields in
#       src/kernel/Platform/BootInfo.cs
```

Runtime evidence: no framebuffer device nodes exist (the system has no `/dev`
devices at all in Phase 1 - see PHASE1-REPORT.md for the deferred console
abstractions), and no log line containing "framebuffer", "fb0" or "GOP" appears
in the serial transcript:

```bash
grep -aiE "framebuffer|fb0|GOP|graphics" serial.log   # no output expected
```

## 5. All output on the serial console

By design the kernel has exactly one output sink: `DebugConsole`
(COM1, 0x3F8, 115200 8N1). Bootloader messages go to the same port; the UEFI
console is only used for the pre-ExitBootServices banner. Verify every line of
`serial.log` came from COM1 by booting with `-display none` (default in
`make run-qemu`) - there is no other display device attached.

## 6. `neutrinoos>` prompt and character echo

Interactive:

```bash
make run-qemu
# then type: hello<Enter>
# expected:
#   neutrinoos> hello
#   neutrinoos>
```

Automated check via piped stdin:

```bash
printf 'hello neutrinoos\n' | timeout 120 ./tools/run-qemu.sh > serial.log 2>&1 || true
grep -a "neutrinoos> hello neutrinoos" serial.log
```

## 7. No C / C++ files in kernel or bootloader

```bash
find src/kernel src/bootloader -name '*.c' -o -name '*.cpp' -o -name '*.h' -o -name '*.hpp' -o -name '*.cc'
#    -> no output
```

The only non-C# code is the pre-existing assembly (`src/bootloader/boot.asm`,
`src/kernel/x64/native.asm`), which is required for privileged instructions and
was already part of ProtonOS.

## 8. Windows 11 build guide

Follow [BUILD-WINDOWS.md](BUILD-WINDOWS.md) on a clean Windows 11 machine:
install WSL2 + Ubuntu 24.04, run `make install-deps`, `make deps`, `make image`,
then `make run-qemu` from the VS Code integrated terminal. The guide includes
troubleshooting for the common failure modes (missing OVMF, missing SDK,
slow TCG emulation without KVM).
