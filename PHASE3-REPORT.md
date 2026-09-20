# Phase 3 Report — VGA Text Console + PS/2 Keyboard

Status: **complete** (all acceptance criteria verified; see
`docs/PHASE3-ACCEPTANCE.md` for the checklist and how to reproduce).

## Summary of changes

**New components**

| Path | Description |
|------|-------------|
| `src/kernel/Platform/Vga/VgaTextDriver.cs` | VGA text-mode driver: mode 3 register set (80×25), 80×50 deltas, CP437 font loader (plane 2), hardware cursor, scroll, blink control, standard DAC palette |
| `src/kernel/Platform/Vga/VgaConsoleDevice.cs` | `/dev/vga0` device: ANSI subset parser, Unicode→CP437 mapping, echo entry, prompt-tail tracking |
| `src/kernel/Platform/Vga/VgaFontData.cs` | Generated CP437 fonts (8×16 + 8×8) from the SeaBIOS VGA BIOS ROM (`build/gen-vga-font.py`) |
| `src/kernel/Platform/Ps2/Ps2Keyboard.cs` | 8042/PS-2 keyboard: IRQ1, scancode set 1, modifiers + Caps Lock LED, ANSI byte synthesis |
| `tests/VgaTest/` | `vga_test.dll` — VGA acceptance test (colors, Clear, cursor read-back, CP437 frames, scroll) |
| `tests/KeyboardTest/` | `keyboard_test.dll` — interactive ConsoleKeyInfo inspector |
| `scripts/test-vga.ps1` | Windows 11 verification script (QEMU via WSL2, monitor `sendkey`, VGA buffer assertion, screendump) |
| `build/` tooling (untracked by design) | `wsl-vga-test.py` acceptance runner, `wsl-rebuild.sh`, `boot-fast.sh`, `boot-vga-on.sh`, `run-acceptance.sh`, `gen-vga-font.py`, `vga-80x50.sh` |

**Modified components**

| Path | Change |
|------|--------|
| `src/kernel/Platform/Consoles/ConsoleAbstractionLayer.cs` | Registers `/dev/vga0`; dual-console echo sink; active-input auto-switching; marker-file boot parameters; `InitializeVgaConsole` |
| `src/kernel/Platform/Serial/LineDiscipline.cs` | Echo sink hook (`SetEchoSink`), managed `FeedByte` entry for the keyboard path |
| `src/kernel/Kernel.cs` | Phase 3 test assemblies loaded/registered/run under markers; `skip-preempt` debug marker |
| `src/kernel/Runtime/MethodTable.cs` | **Fix**: interface-dispatch resolver path made print-free (see Blockers) |
| `src/kernel/x64/native.asm` | **Fix**: `RhpInitialDynamicInterfaceDispatch` x64 stack alignment (40-byte frame) |
| `Makefile` | `vga_test`/`keyboard_test` build targets + image entries; `run-qemu-vga` target |
| `docs/PHASE3-DESIGN.md`, `docs/PHASE3-ACCEPTANCE.md`, `PHASE3-REPORT.md` | New documentation |

## Acceptance results

* Automated suite (`build/run-acceptance.sh`, TCG, ≤60 s windows):
  * 80×25: **PASS** — shell prompt, PS/2 echo, backspace `\x08 \x20 \x08`,
    line editing, Up-arrow history, Ctrl+C + continued shell, screendump
    (720×400, 16 text rows, ~5 900 lit pixels).
  * 80×50: **PASS** — same interaction set; screendump shows 16 text rows
    at the 8-pixel cell pitch with the 8×8 font.
* Visual verification: VGA screendumps render crisp CP437 text
  (`[OK] Kernel initialization complete`, `[SHELL] NeutrinoOS console
  ready.`, `neutrinoos>`) in both modes.
* Phase 2 regression: serial-only configurations boot and behave as
  before (LineDiscipline-only and baseline-CAL builds re-tested during
  bisection; `boot-fast.sh` exercises the `console-vga-off` path).

## Blockers encountered and resolved

1. **Post-CAL boot stall (page-fault/GP storm, triple fault).** With any
   build that compiled a *second* `IConsoleDevice` implementation, the
   compiler stopped devirtualizing the console multiplexer's interface
   calls, activating the dynamic interface-dispatch path
   (`RhpInitialDynamicInterfaceDispatch` → `RhpResolveInterfaceMethod`)
   for the first time. Two latent bugs fired:
   * the resolver's `DebugConsole` prints re-enter the console stack
     (multiplexer → interface call → resolver → print → …) causing
     unbounded recursion that consumed ~264 MB of stack and overwrote
     kernel code, and
   * the asm stub left `RSP` misaligned by 8 bytes for the resolver call,
     faulting on aligned SSE spills.
   Fixes: removed all console prints from the resolver path (documented
   as an invariant in `PHASE3-DESIGN.md` §7) and corrected the stub's
   stack frame (40 bytes). Root-caused via bisection (LineDiscipline-only
   build passed; CAL-with-new-types build failed), gdb watchpoints on the
   corrupted code page, and register/dispatch inspection.
2. **Blank glyphs on VGA under OVMF.** OVMF has no CSM/legacy option-ROM
   execution, so the VGA font plane (plane 2) is never populated. Fixed
   by embedding the standard CP437 fonts in the kernel
   (`VgaFontData.cs`, generated from the SeaBIOS ROM) and loading them
   via the INT 10h AH=11h sequence, with the BIOS-compatible 32-byte
   per-character layout (verified against QEMU's renderer).
3. **Palette/attribute-controller bugs.** The attribute-port flip-flop
   protocol and the color-plane-enable register (AR 0x12) were programmed
   incorrectly, producing blue backgrounds / black text; fixed and
   documented.
4. **WSL2 quirks.** WSL instances are evicted aggressively; every build
   or test step is a single self-contained invocation with a 60-second
   bound. QEMU under WSL2 KVM can hang the monitor during rapid
   `sendkey` bursts, so the automated acceptance runs under TCG (boots in
   ~7 s, well inside the window).

## Deviations from the specification

1. **Boot parameters**: implemented as marker files
   (`console-vga-off`, `console-vga-80x50`, `console-active-vga`)
   consistent with NeutrinoOS's existing marker mechanism
   (`skip-boot-tests`), instead of literal command-line options
   `console.vga=off` / `console.vga.mode=80x50`.
2. **VirtualBox**: VMSVGA cannot expose the text framebuffer; VBoxVGA is
   the documented alternative (see `docs/PHASE3-ACCEPTANCE.md` §5).
   Automated verification targets QEMU (`-vga std`).
3. **Key repeat**: the 8042 retains its firmware-default typematic
   settings; Phase 3 does not add rate configuration (documented in
   `docs/PHASE3-DESIGN.md` §8).
4. **`run-qemu-vga`** uses WSLg's GTK window on Windows 11 (no separate VNC
   path); `scripts/test-vga.ps1` performs the monitor-driven key injection
   and screendump assertions required by the spec.

## Deferred to later phases

* TTY multiplexing / virtual terminals; graphics modes; mouse.
* Runtime font upload (`INT 10h AH=11h` emulation for application code).
* Configurable key repeat and non-US keyboard layouts.
* Re-enabling interface-call *caching* in the dynamic dispatch stub
  (current stub re-resolves on every call; correctness first, an L1
  cache probe is a Phase 4+ performance item).
* JIT validation/networking/shell/utilities remain Phase 4+ concerns.

## Open questions / assumptions

* The acceptance runner and helper scripts live in `build/` (git-ignored
  by repository convention); they are required to reproduce the automated
  checks on a fresh checkout.
* `sendkey`-driven typing assumes the default 8042 translation; real
  hardware verification of the scancode matrix is covered by the
  interactive `keyboard_test.dll` (`run-keyboard-test` marker).
