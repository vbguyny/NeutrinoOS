# ROLE

You are a senior systems engineer specializing in x86-64 kernel bring-up,
VGA text-mode drivers, PS/2 keyboard controllers, terminal emulation, and
managed runtime development in C# on bare metal. You are assisting in
Phase 3 of a custom operating system project.

# PROJECT CONTEXT

Project name: NeutrinoOS
Base project: ProtonOS (a managed OS written entirely in C# using bflat's
  zero-library mode, with a Tier-0 JIT compiler).

Phase 1 status: COMPLETE. All graphics, framebuffer, and GOP code removed.
  Bootloader initializes COM1 at 0x3F8, 115200 8N1. Kernel boots under
  QEMU+OVMF and reaches a minimal banner + character echo on the serial
  console.

Phase 2 status: COMPLETE. A production-quality UART 16550 driver with
  interrupt-driven RX/TX is registered as `/dev/ttyS0`. A line discipline
  layer provides canonical and raw modes, backspace editing, Ctrl+C/Ctrl+D/
  Ctrl+U handling, and 32-entry arrow-key history. A Console Abstraction
  Layer (CAL) with `IConsoleDevice` and `ConsoleMultiplexer` routes output
  to all registered console devices and input from one designated active
  input device. `korlib` now implements `System.Console`,
  `System.IO.TextWriter`/`TextReader`, `System.ConsoleColor`,
  `System.ConsoleKey`/`ConsoleKeyInfo`/`ConsoleModifiers`,
  `System.Text.Encoding.UTF8`, and `System.Environment` sufficient to run
  a standard .NET 10 console application that does interactive text I/O.

Target of the overall project: A console-only, headless managed OS that runs
  .NET 10 console applications and utilities on bare metal, with TCP/IP
  networking, SSH, curl, and the ability to host .NET web apps. No GUI,
  no graphical framebuffer, no window manager.

# ENVIRONMENT

- Host OS: Windows 11 (x64)
- IDE: Visual Studio Code (latest stable) with the Remote - WSL extension
- Shell: PowerShell 7 on the host; bash inside WSL2 Ubuntu 24.04
- Toolchain (already installed in WSL2 from Phase 1):
  - .NET SDK 10.0
  - bflat (installed via `dotnet tool install -g bflat`)
  - clang / ld.lld (LLVM 17+)
  - GNU make
  - Python 3.11+
  - qemu-system-x86_64 with OVMF firmware
  - git
- Phase 2 boot verification used:
  `make run-qemu-serial` and `make run-qemu-serial-log`, which launch QEMU
  with `-display none -serial stdio` (or a log file) and boot to a
  NeutrinoOS shell prompt on the serial console.
- Windows 11 host is assumed to have VirtualBox 7.x installed for later
  manual verification (Phase 7 will formalize this; Phase 3 only needs to
  not break VirtualBox compatibility).
- For Phase 3, QEMU will be launched with BOTH a serial console AND a VGA
  display window so the VGA text output can be visually verified from
  Windows. Recommended QEMU invocation:
  `qemu-system-x86_64 -machine q35 -m 2G -bios OVMF_CODE.fd \
     -drive if=pflash,format=raw,file=OVMF_VARS.fd \
     -drive file=neutrinoos.img,format=raw,if=virtio \
     -device VGA,vgamem_mb=16 \
     -serial stdio`
  The QEMU window opens on Windows; the serial stream continues to appear
  in the WSL2 terminal.

# PHASE 3 GOAL — "VGA TEXT CONSOLE + PS/2 KEYBOARD"

Add a VGA text-mode console as a second `IConsoleDevice` and a PS/2
keyboard driver as its input source, both written in C#. The CAL
multiplexer from Phase 2 must route output to both `ttyS0` and `vga0`
simultaneously, and route input from whichever device the user is typing
on. Phase 3 is complete when a user can interact with the NeutrinoOS shell
entirely from the QEMU VGA window (keyboard + text display), with the same
line-editing, history, and ANSI color behavior that Phase 2 provided on the
serial console.

Phase 3 does NOT include: GUI, framebuffer graphics, mouse, VGA graphics
modes (mode 13h, VBE, etc.), TTY multiplexing between multiple VGA virtual
terminals, or any graphical terminal emulator. It is strictly 80x25 or
80x50 character-cell text mode.

# DETAILED TASKS

## Task 1 — VGA text-mode driver (`/dev/vga0`)

Create a new driver project (e.g., `Drivers.VgaText`) written in C# only.
Requirements:

- **VGA text buffer**: access the standard VGA text framebuffer at physical
  address `0xB8000` (mapped through the kernel's VMM into the higher-half
  kernel address space; do not access physical memory directly from C#).
  Each cell is 2 bytes: low byte = character (CP437 code page), high byte =
  attribute (bits 0–3 foreground, bits 4–6 background, bit 7 blink).
- **Modes**: support 80x25 (BIOS default) and 80x50 (VGA 8x8 font, set via
  the VGA sequencer and CRTC registers). Default to 80x25 for maximum
  compatibility with QEMU's OVMF; make 80x50 selectable via a kernel boot
  parameter (`console.vga.mode=80x50`).
- **Cursor**: hardware cursor driven by the VGA CRTC registers (index 0x0E
  and 0x0F). Provide `SetCursorPosition`, `GetCursorPosition`, and
  `SetCursorShape(start, end)`.
- **Scrolling**: when the cursor moves past the last row, scroll the
  buffer up by one row (memmove-style copy of the character cells) and
  blank the new bottom row. Use the VGA hardware "vertical retrace"
  status bit (input port 0x3DA, bit 3) to avoid tearing during large
  scrolls, or accept tearing in Phase 3 and document it as a known
  limitation.
- **Character output**: translate `\n` to carriage-return + line-feed,
  handle `\r`, `\t` (advance to next 8-column boundary), and `\b` (move
  cursor left one column, do not erase — the line discipline is
  responsible for erasing).
- **ANSI escape sequence support**: implement the same subset that Phase 2
  emits on serial, so that `System.Console` code produces identical output
  on both consoles:
  - `ESC [ 2 J` (clear screen)
  - `ESC [ H` and `ESC [ row ; col H` (cursor position)
  - `ESC [ n m` for SGR colors 30–37, 40–47, 90–97, 100–107, plus
    `0` (reset), `1` (bold), `7` (reverse video)
  - `ESC [ K` (erase to end of line) and `ESC [ 2 K` (erase entire line)
  - `ESC [ A/B/C/D` (cursor up/down/right/left)
  - `ESC [ s` and `ESC [ u` (save/restore cursor position)
- **CP437 font**: use the VGA ROM 8x8 font (present at physical address
  `0xC0000` in VGA BIOS, or embed the standard IBM CP437 8x8 font table in
  the driver as a `byte[]` constant to avoid dependence on VGA BIOS). For
  the 80x50 mode, load the 8x8 font into the VGA character generator
  planes via the sequencer.
- **Public API** (match the `IConsoleDevice` interface from Phase 2's CAL,
  but only add methods that are meaningful for text mode):
  - `void Initialize(VgaTextMode mode)` — modes: `Text80x25`, `Text80x50`.
  - `void Write(char c)`, `void Write(ReadOnlySpan<char> s)`
  - `void Clear()`
  - `void SetCursorPosition(int left, int top)` / `(int Left, int Top) GetCursorPosition()`
  - `ConsoleColor ForegroundColor { get; set; }` / `ConsoleColor BackgroundColor { get; set; }`
  - `void Flush()` — no-op; VGA text writes go directly to the buffer.
  - `void SetBlinkEnabled(bool enabled)` — toggles the attribute bit 7 meaning.
- Register the driver as `/dev/vga0` with the VFS (this may require a
  small amount of VFS plumbing if Phase 1/2 did not already expose a
  registration API for character devices; if so, add the minimal plumbing
  and document it).
- Implement `IConsoleDevice` on top of this driver so the CAL can register it.

## Task 2 — PS/2 keyboard driver

Create a new driver project (e.g., `Drivers.Ps2Keyboard`) written in C# only.
Requirements:

- **Controller**: communicate with the 8042 PS/2 controller at I/O ports
  0x60 (data) and 0x64 (status/command). Drain the output buffer on init,
  set the configuration byte to enable IRQ1 and disable translation if
  scancode set 2 is used; for maximum compatibility with QEMU and
  VirtualBox, **use scancode set 1** (XT scancodes) with translation
  enabled, which is the default after power-on and is what both QEMU and
  VirtualBox emulate.
- **Interrupt**: register an IRQ1 handler with the kernel's interrupt
  framework. On each IRQ, read the scancode from port 0x60, feed it to the
  decoder, and send EOI to the PIC (or APIC, depending on the kernel's
  interrupt routing).
- **Decoder**: implement a small state machine that handles:
  - Make codes (key press) and break codes (key release, high bit set).
  - Extended scancodes (prefix `0xE0`) for arrow keys, Home, End, Delete,
    PgUp, PgDn, and the keypad `/` and Enter.
  - Extended `0xE1` sequences for Pause/Break (optional; log and ignore is
    acceptable).
  - Shift (left `0x2A`, right `0x36`), Ctrl (left `0x1D`, right `0xE0 0x1D`),
    Alt (left `0x38`, right `0xE0 0x38`), Caps Lock (`0x3A`, toggle on make
    only), Num Lock (`0x45`, toggle on make only), Scroll Lock (`0x46`,
    toggle on make only).
  - Key repeat: after a 500 ms initial delay, repeat at ~30 Hz while a
    key is held. Repeat is driven by the kernel's timer tick (100 Hz from
    Phase 1) — no separate timer needed.
  - Produce `ConsoleKeyInfo` values with the correct `ConsoleKey`,
    `ConsoleModifiers`, and `char` (already mapped through Caps Lock and
    Shift, using a US-layout scancode-to-char table).
- **ASCII/CP437 mapping**: for scancodes that produce printable characters,
  map to the correct CP437 code point (e.g., scancode 0x02 with Shift
  produces `!`, scancode 0x03 with Shift produces `@`, etc.). Use a
  static table.
- **Public API**:
  - `void Initialize()`
  - `bool TryReadKey(out ConsoleKeyInfo key)` — non-blocking, drained from
    the ring buffer.
  - `ConsoleKeyInfo ReadKey(bool intercept)` — blocking, yields to scheduler.
  - `void SetLeds(bool caps, bool num, bool scroll)` — send `0xED` command
    to the 8042 to set keyboard LEDs (best effort; QEMU and VirtualBox
    support this).
- Register the driver's input stream with the CAL as the input source for
  `/dev/vga0`. Do NOT register it as a global input source when serial is
  the active input — the multiplexer should switch active input based on
  which device produced the most recent input event (see Task 4).

## Task 3 — VGA text-mode ANSI terminal emulator

The VGA text driver must implement a small ANSI terminal emulator so that
`System.Console` calls from `korlib` (which already emit ANSI escape
sequences to support color and cursor positioning on serial) work
identically on VGA. Requirements:

- Maintain a small parser state machine (Ground, Escape, CSI, Parameters,
  Intermediate) that consumes bytes written via `Write(char)` and interprets
  escape sequences rather than printing them as glyphs.
- On a complete CSI sequence, dispatch to the VGA driver's cursor,
  color, clear, and scroll operations.
- For printable characters, write to the VGA text buffer at the current
  cursor position using the current foreground/background colors, then
  advance the cursor.
- Handle the following sequences (the same subset documented for the
  Phase 2 serial path, for parity):
  - `ESC [ 2 J`, `ESC [ H`, `ESC [ row ; col H`
  - `ESC [ n m` (SGR: 0, 1, 7, 30–37, 40–47, 90–97, 100–107)
  - `ESC [ K`, `ESC [ 2 K`
  - `ESC [ A/B/C/D`
  - `ESC [ s`, `ESC [ u`
- If an unrecognized escape sequence is received, silently consume it
  (do not print the raw bytes). Log the sequence at debug level if a
  debug log channel is available.

## Task 4 — Console Abstraction Layer integration

Extend the Phase 2 CAL to support multiple console devices with automatic
input switching.

- Register `/dev/vga0` as a second `IConsoleDevice` after `/dev/ttyS0`.
  Both must receive all `Write`/`WriteLine` output from `ConsoleMultiplexer`
  (so a user sees the same output whether they are on serial or VGA).
- **Active input switching**: the CAL must track which device most recently
  produced input. When a key is typed on the PS/2 keyboard, the active
  input device becomes `vga0`. When a byte arrives on `ttyS0`, the active
  input becomes `ttyS0`. Input is read from the active device only; the
  other device's input buffers remain drained but discarded (or buffered
  for later, at the driver's discretion — buffering is simpler and
  preferred).
- Provide a kernel boot parameter `console.active=serial|vga|auto` (default
  `auto`) to force a specific active input device or allow automatic
  switching.
- Provide a `ConsoleMultiplexer.SetActiveInput(IConsoleDevice)` API for
  future use by the shell.
- Ensure the shell from Phase 2 (which calls `Console.ReadLine()`) picks up
  the newly active device without any code changes.

## Task 5 — Kernel integration and boot sequence

- Initialize the VGA text driver immediately after the serial console driver
  in the kernel init sequence, so that the boot banner appears on both
  devices.
- Initialize the PS/2 keyboard driver after the scheduler and IDT are up
  (so the IRQ1 handler can be registered and keyboard input can yield to
  other tasks if needed).
- Ensure the boot sequence logs via `System.Console.WriteLine` so the
  banner appears on both serial and VGA. Sample expected output:

  NeutrinoOS v0.3 (x86-64 UEFI)
  [BOOT] Loading kernel...
  [BOOT] Memory: 2048 MB available
  [KERNEL] GDT/IDT initialized
  [KERNEL] PMM: 524288 pages (2048 MB)
  [KERNEL] VMM: 4-level paging, higher-half kernel
  [KERNEL] Scheduler: APIC timer @ 100 Hz
  [GC] Compacting GC initialized
  [CONSOLE] Serial console initialized (ttyS0 @ 115200 8N1)
  [CONSOLE] VGA text console initialized (vga0 @ 80x25)
  [INPUT] PS/2 keyboard initialized (IRQ1)
  [SHELL] NeutrinoOS console ready.
  neutrinoos>

- Add a kernel boot parameter `console.vga=off` to disable the VGA console
  entirely (useful for headless deployments and for measuring boot time
  without VGA init). Default is `on`.

## Task 6 — QEMU and VirtualBox verification

- Add a `make run-qemu-vga` target that launches QEMU with both serial and
  VGA output, opening a window on Windows 11:

  qemu-system-x86_64 -machine q35 -m 2G \
    -bios /usr/share/OVMF/OVMF_CODE.fd \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE.fd \
    -drive if=pflash,format=raw,file=build/OVMF_VARS.fd \
    -drive file=build/neutrinoos.img,format=raw,if=virtio \
    -device VGA,vgamem_mb=16 \
    -serial stdio

  Document in the README that the QEMU window opens on the Windows desktop
  and that keyboard input to that window is captured by the guest.

- Add a `make run-vbox-test` target (or update the existing VirtualBox
  documentation) that verifies VGA text output works under VirtualBox 7.x
  with the default VGA controller (VMSVGA) — note that VirtualBox's VMSVGA
  controller may not expose the standard VGA text framebuffer at 0xB8000.
  If VMSVGA does not support text mode, document that the VGA text driver
  works under QEMU and under VirtualBox with the "VBoxVGA" controller, and
  note the limitation for the user.

- Add a `scripts/test-vga.ps1` PowerShell script for Windows 11 that:
  - Launches QEMU with VGA output to a VNC or SDL window on the host.
  - Uses the QEMU monitor to send keystrokes (via `sendkey`) to verify
    keyboard input and line editing on the VGA console.
  - Captures a screenshot via `screendump` and asserts that the expected
    prompt text appears.

## Task 7 — Testing and documentation

- Add a C# test program (`tests/vga_test.dll`) that exercises:
  - `Console.Write` / `WriteLine` with strings and colored output.
  - `Console.Clear()` and cursor positioning.
  - `Console.SetCursorPosition` and cursor position read-back.
  - Extended character output (box-drawing characters from CP437).
  - Line editing with backspace and arrow-key history (shared behavior
    with the Phase 2 serial console).
- Add a `tests/keyboard_test.dll` that prints the `ConsoleKeyInfo` of every
  key pressed, to verify the PS/2 decoder against a manual keypress matrix.
- Produce `docs/PHASE3-DESIGN.md` describing:
  - The VGA text buffer layout, CRTC register usage, and font handling.
  - The ANSI terminal emulator state machine.
  - The PS/2 keyboard scancode decoder state machine and the scancode-to-
    CP437 table.
  - The CAL multiplexer's active-input switching rules.
  - Known limitations (e.g., no TTY multiplexing, no graphics mode, CP437
    only, US keyboard layout only).
- Produce `docs/PHASE3-ACCEPTANCE.md` listing the acceptance criteria below
  and how to verify each from Windows 11.
- Produce `PHASE3-REPORT.md` summarizing changes, blockers, and deviations.

# CONSTRAINTS

- All code must be C# (plus the existing assembly intrinsics). Do NOT add
  C or C++ files to the kernel, bootloader, or any driver.
- Do NOT introduce a graphical framebuffer, GUI, mouse support, VGA
  graphics mode (mode 13h, VBE, VESA), or a window manager. This phase is
  strictly character-cell text mode.
- Do NOT introduce TTY multiplexing between multiple VGA virtual terminals;
  a single VGA console is sufficient for Phase 3.
- Do NOT use the term "TTY" as a project name or suffix. It is fine to use
  the Unix term "tty" in device paths (`/dev/ttyS0`) and documentation.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to ProtonOS.
- Do NOT scope-creep into Phase 4+ (JIT validation, networking, shell
  parsing, utilities, SSH, curl, web hosting). If a Phase 4+ concern
  arises, note it in the "Deferred to later phases" section.
- Every public type and method added to the kernel, drivers, or `korlib`
  must have XML doc comments describing its Phase 3 semantics and any
  deviations from the official .NET BCL.
- All user-visible strings must say "NeutrinoOS".

# DELIVERABLES

1. VGA text-mode driver (`/dev/vga0`) with 80x25 and 80x50 modes, hardware
   cursor, scrolling, CP437 font, and an ANSI escape sequence parser.
2. PS/2 keyboard driver with IRQ1, scancode set 1 decoding, key repeat,
   modifier tracking, and `ConsoleKeyInfo` production.
3. Extended Console Abstraction Layer with dual-device output routing and
   automatic active-input switching between serial and VGA.
4. Kernel integration: VGA and keyboard initialized in the correct order,
   boot banner visible on both consoles, `console.vga` and
   `console.active` boot parameters.
5. `make run-qemu-vga` target and updated VirtualBox documentation.
6. `scripts/test-vga.ps1` PowerShell script for Windows 11.
7. `tests/vga_test.dll` and `tests/keyboard_test.dll` source and make targets.
8. `docs/PHASE3-DESIGN.md`, `docs/PHASE3-ACCEPTANCE.md`, and
   `PHASE3-REPORT.md`.

# ACCEPTANCE CRITERIA

Phase 3 is complete when ALL of the following are true:

- [ ] `make image` and `make run-qemu-vga` boot to a NeutrinoOS banner
      visible on BOTH the QEMU VGA window and the WSL2 serial terminal,
      with no regressions from Phase 2.
- [ ] Typing on the QEMU VGA window's keyboard echoes characters on the
      VGA console at the `neutrinoos>` prompt.
- [ ] Backspace erases the last typed character on VGA (`\b \b` behavior).
- [ ] Enter submits the line and the minimal shell echoes it back on VGA.
- [ ] Ctrl+C clears the current line, prints `^C`, and re-prompts on VGA.
- [ ] Up/down arrows recall history on VGA with correct redraw.
- [ ] `Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("x");
      Console.ResetColor();` produces red text on VGA.
- [ ] `Console.Clear()` clears the VGA screen and subsequent output begins
      at the top-left.
- [ ] `Console.SetCursorPosition(10, 5)` positions the VGA hardware cursor
      at column 10, row 5.
- [ ] Typing on the serial console and typing on VGA both work
      interchangeably; the active input device switches automatically to
      whichever received the most recent input.
- [ ] Pressing Caps Lock toggles the Caps Lock LED on the QEMU VGA window
      (visible in the QEMU status bar or via `SetLeds` verification).
- [ ] Extended characters (e.g., CP437 box-drawing `0xC4`, `0xB3`, `0xDA`,
      `0xBF`) render correctly on VGA.
- [ ] The 80x50 mode is selectable via `console.vga.mode=80x50` boot
      parameter and renders correctly.
- [ ] `console.vga=off` disables VGA output entirely; boot still succeeds
      on serial only.
- [ ] No C or C++ files exist in the kernel, bootloader, or driver
      directories.
- [ ] `docs/BUILD-WINDOWS.md` (from Phase 1) and `docs/PHASE2-ACCEPTANCE.md`
      still work, and `docs/PHASE3-ACCEPTANCE.md` provides step-by-step
      verification for every checklist item above from a fresh Windows 11
      machine.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps mapped to the seven tasks
   above.
2. **Repository layout** — the target directory tree after Phase 3,
   highlighting new and modified files.
3. **Code changes** — for each file to be created, modified, or deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created files) OR a unified diff
     (for modifications) OR a precise description (for deletions).
   - For large files (e.g., the full VGA driver with the ANSI parser, or
     the PS/2 scancode table), provide the complete source; do not
     abbreviate with "..." unless the omitted region is boilerplate that
     is explicitly described. The scancode-to-CP437 table must be
     complete.
4. **Kernel integration notes** — a short section describing exactly where
   in the Phase 2 kernel init sequence the VGA driver and keyboard driver
   are initialized, and in what order relative to the serial driver, CAL,
   and shell launch.
5. **Build and test commands** — exact WSL2 bash commands and PowerShell
   commands for Windows 11 to build, run, and verify Phase 3.
6. **Acceptance checklist** — reproduce the checklist above, with a
   one-line note for each item explaining how it is satisfied.
7. **Deferred to later phases** — anything that came up that belongs to
   Phase 4+ (JIT validation, networking, VFS plumbing for device nodes,
   syscall plumbing, shell parsing, utilities, SSH, curl, web hosting).
8. **Open questions / assumptions** — anything ambiguous about the Phase 2
   output, the existing `korlib` structure, or the ProtonOS conventions
   that you assumed, and how the user can verify or correct them.

If any part of the Phase 2 output is unclear, or if the existing `korlib`
layout does not match your assumptions, state your assumptions explicitly
and proceed with a reasonable layout consistent with a bflat-based managed
kernel, noting where the user must adjust paths.

Do not skip ahead to Phase 4–8. Scope discipline is mandatory: Phase 3 only.