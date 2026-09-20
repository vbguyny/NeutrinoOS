# Phase 3 Acceptance — VGA Text Console + PS/2 Keyboard

Step-by-step verification for every Phase 3 acceptance criterion, runnable
from a fresh Windows 11 machine (WSL2 + QEMU as in
`docs/BUILD-WINDOWS.md`).

## 0. Prerequisites (one-time)

In WSL2 (`Ubuntu-24.04` as root):

```bash
apt-get install -y qemu-system-x86 qemu-utils ovmf mtools dosfstools numactl
```

Build the toolchain and image (from the repository root in WSL2 or via
`./build.sh` / `build/wsl-rebuild.sh`):

```bash
make deps      # first time only
make image
```

The image is `build/x64/neutrinoos.img`; OVMF firmware files come from
`/usr/share/OVMF/` (the `run-qemu-*` targets copy `OVMF_VARS_4M.fd` into
`build/x64/OVMF_VARS.fd` automatically).

## 1. Automated acceptance (recommended)

From Windows PowerShell (repository root):

```powershell
powershell -File scripts\test-vga.ps1
```

This boots the image under QEMU (inside WSL2), drives the emulated PS/2
keyboard via the QEMU monitor, reads the VGA text buffer at `0xB8000`
and asserts the `neutrinoos>` prompt and PS/2 echo/execution appear on
VGA; it also captures a screendump to `build/x64/vga-screen.ppm`.

The full end-to-end suite (typing, backspace echo, history recall,
Ctrl+C, screendump analysis; 80×25 and 80×50 modes) runs from WSL:

```bash
bash build/run-acceptance.sh                                   # 80x25
bash build/run-acceptance.sh --extra-marker /tmp/console-vga-80x50 --expect-rows 6
```

`RESULT: PASS` with `ACCEPTANCE-RC=0` is required for both.

## 2. Interactive run (QEMU window)

```bash
make run-qemu-vga
```

A QEMU GTK window opens on the Windows desktop (WSLg) showing the VGA
console; the serial console is attached to the terminal. Both consoles
show the boot banner and the `neutrinoos>` shell prompt. Quit with
Ctrl+A X in the serial terminal (or close the VGA window).

## 3. Acceptance checklist

Each item lists the exact check. "Automated" = covered by
`build/run-acceptance.sh`; "PS" = covered by `scripts/test-vga.ps1`;
"Manual" = reproduce in the `run-qemu-vga` window.

| # | Criterion | How to verify |
|---|-----------|----------------|
| 1 | `make image` + VGA boot: banner on BOTH QEMU VGA and serial, no Phase 2 regressions | Automated: `boot-fast.sh` reaches the shell; `run-acceptance.sh` checks the shell prompt on serial; the QEMU window/screendump shows `[OK] Kernel initialization complete`, `[SHELL] NeutrinoOS console ready.`, `neutrinoos>` on VGA. Phase 2 regression: `bash build/wsl-conio-runner.py` (or `make run-console-test`) still passes. |
| 2 | Typing on the QEMU window keyboard echoes on VGA at the prompt | Automated: the runner types `echo hello` via `sendkey` (emulated PS/2 IRQ1) and checks the echo + execution; Manual: type in the window. |
| 3 | Backspace erases the last character on VGA (`\b \b` behavior) | Automated: types `ab`, Backspace, `z`, Enter -> asserts `\x08 \x20 \x08` on the line discipline and that `az` executes. |
| 4 | Enter submits; the shell echoes the line back | Automated: `\r\necho hello\r\n` observed; Manual: press Enter. |
| 5 | Ctrl+C clears the line, prints `^C`, re-prompts | Automated: asserts `^C` + a fresh prompt + a subsequent command works. |
| 6 | Up/down arrows recall history with correct redraw | Automated: submits `one`, `two`, presses Up, Enter -> `two` runs again. |
| 7 | `Console.ForegroundColor = Red; WriteLine("x"); ResetColor()` produces red text | `run-vga-test` marker: `vga_test.dll` prints red/green samples on VGA (visible in the QEMU window); the echo/color path shares `ComposeAttribute`. |
| 8 | `Console.Clear()` clears VGA and homes the cursor | `run-vga-test` marker: the test asserts `CursorLeft/ Top == 0` after Clear (PASS line on console). |
| 9 | `Console.SetCursorPosition(10,5)` positions the hardware cursor | `run-vga-test`: asserts read-back of `(10,5)` (PASS line). The hardware cursor follows via CRTC 0x0E/0x0F. |
| 10 | Serial and VGA input work interchangeably; active input follows last input | Manual: type on serial, then on the VGA window; echo appears on both; the active input switches (see `console-active-vga` marker to bias the initial choice). |
| 11 | Caps Lock toggles the keyboard LED | Manual: press Caps Lock in the QEMU window; the QEMU status bar shows the LED state change (the driver issues the `0xED` LED command). |
| 12 | Extended characters (CP437 `0xC4`, `0xB3`, `0xDA`, `0xBF`) render | `run-vga-test`: prints box-drawing frames on VGA (visible in the window/screendump). |
| 13 | 80×50 mode selectable and renders correctly | `bash build/run-acceptance.sh --extra-marker /tmp/console-vga-80x50 --expect-rows 6` (adds the `console-vga-80x50` marker); screendump analysis asserts >= 6 text rows at the 8-pixel cell pitch. Manual: add the marker file to the image and boot `make run-qemu-vga`. |
| 14 | `console.vga=off` disables VGA entirely; serial boot still succeeds | Marker-file equivalent: add `console-vga-off` to the image (`mdel/mcopy` or `--extra-marker`), then boot; the log shows no `[VGA-*]` traces and the shell works serially. `build/boot-fast.sh` runs exactly this configuration. |
| 15 | No C/C++ files in kernel/bootloader/driver directories | `find src -name '*.c' -o -name '*.cpp'` returns nothing (except the pre-existing NASM `.asm` intrinsics, which are allowed). |
| 16 | Phase 1/2 docs still valid; this file provides step-by-step verification | `docs/BUILD-WINDOWS.md` and `docs/PHASE2-ACCEPTANCE.md` unchanged; this document covers every checklist item. |

## 4. Boot-parameter markers (Phase 3)

Marker files on the boot volume (FAT root of `neutrinoos.img`), e.g.:

```bash
# add the 80x50 marker to the image
echo 1 > /tmp/console-vga-80x50
mcopy -o -i build/x64/neutrinoos.img /tmp/console-vga-80x50 ::/console-vga-80x50
# remove it
mdel -i build/x64/neutrinoos.img ::/console-vga-80x50
```

| Marker | Effect |
|--------|--------|
| `console-vga-off` | Serial-only boot (no VGA device) |
| `console-vga-80x50` | 80×50 text mode (8×8 font) |
| `console-active-vga` | VGA is the initial active input |
| `skip-echo` / `skip-ps2` / `skip-vga-register` | Partial bring-up (diagnostics) |
| `run-vga-test` | Runs `vga_test.dll` after init |
| `run-keyboard-test` | Runs the interactive `keyboard_test.dll` |

## 5. VirtualBox notes (VMSVGA limitation)

VirtualBox 7.x with the default **VMSVGA** controller does not expose the
legacy `0xB8000` VGA text framebuffer to the guest, so the VGA text
console cannot be used there. Boot with the **VBoxVGA** controller
instead:

```powershell
# convert the image and create the VM (see docs/BUILD-WINDOWS.md)
VBoxManage modifyvm NeutrinoOS --graphicscontroller vboxvga
VBoxManage startvm NeutrinoOS
```

With VBoxVGA the VGA console works as on QEMU. VMSVGA is documented as
unsupported for the text console in Phase 3.

## 6. Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| Screen is blank except a cursor | Font plane not loaded; ensure the kernel image is current (rebuild). The kernel loads CP437 fonts itself (see `PHASE3-DESIGN.md` §2). |
| Text renders as a pattern of dots | Wrong font storage layout / character-map select; rebuild from a current checkout. |
| Boot stalls right after CAL init | Kernel-runtime interface-dispatch issue; see `PHASE3-DESIGN.md` §7. |
| Monitor hangs during `sendkey` bursts in WSL2 | WSL2 KVM quirk; run the acceptance in TCG mode (the provided scripts default to it). |
