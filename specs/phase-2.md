# ROLE

You are a senior systems engineer specializing in x86-64 kernel bring-up,
UART/serial driver development, and .NET runtime library (BCL) surface
implementation on bare metal. You are assisting in Phase 2 of a custom
operating system project.

# PROJECT CONTEXT

Project name: NeutrinoOS
Base project: ProtonOS (a managed OS written entirely in C# using bflat's
  zero-library mode, with a Tier-0 JIT compiler).
Phase 1 status: COMPLETE. The fork has been stripped of all graphics,
  framebuffer, and GOP code. The bootloader initializes COM1 at 0x3F8,
  115200 8N1, and redirects all boot output to the serial port. The kernel
  boots under QEMU+OVMF and reaches a minimal banner + character echo on
  the serial console. A minimal UART write path exists but is not yet a
  fully-featured driver, and `System.Console` in `korlib` is only partially
  implemented.

Target of the overall project: A console-only, headless managed OS that runs
  .NET 10 console applications and utilities on bare metal, with TCP/IP
  networking, SSH, curl, and the ability to host .NET web apps. No GUI,
  no framebuffer, no window manager.

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
- Phase 1 boot verification used:
  `qemu-system-x86_64 -machine q35 -m 2G -bios OVMF_CODE.fd \
     -drive if=pflash,format=raw,file=OVMF_VARS.fd \
     -drive file=neutrinoos.img,format=raw,if=virtio \
     -serial stdio -display none`
- For interactive serial testing from Windows, the user connects to the QEMU
  serial stream via the WSL2 terminal, or via `socat`/named pipe as
  documented in Phase 1's `docs/BUILD-WINDOWS.md`.

# PHASE 2 GOAL — "SERIAL CONSOLE"

Replace the Phase 1 minimal UART write path with a production-quality
serial console subsystem, and implement enough of `System.Console` and the
related `System.IO` types in `korlib` that a standard .NET 10 console
application can perform interactive text I/O on bare metal.

Phase 2 is complete when an interactive line-oriented shell prompt over
the serial port supports: echoing typed characters, backspace editing,
Enter to submit a line, arrow-key history (up/down), Ctrl+C to cancel the
current line, and ANSI color output — all driven through `System.Console`
calls made from C# code compiled by bflat.

Phase 2 does NOT include: a shell command parser, external utilities,
networking, SSH, or .NET 10 JIT execution. Those are later phases.

# DETAILED TASKS

## Task 1 — Production UART 16550 driver (COM1, 0x3F8)

Create a new driver project (e.g., `Drivers.Serial`) or extend the existing
Phase 1 driver, written in C# only. Requirements:

- Initialize COM1 at I/O port 0x3F8 with:
  - Baud rate divisor for 115200 (and a table for 9600/19200/38400/57600
    selectable at runtime via a kernel parameter or API).
  - 8 data bits, no parity, 1 stop bit (8N1).
  - FIFO enabled, 14-byte trigger level.
  - Modem control register set for DTR/RTS.
- Provide the following public API (names are suggestions; match the
  existing NeutrinoOS driver conventions if they differ):
  - `void Initialize(uint baudRate)`
  - `void WriteByte(byte b)`
  - `void Write(ReadOnlySpan<char> s)` — translate `\n` to `\r\n`.
  - `bool TryReadByte(out byte b)` — non-blocking.
  - `byte ReadByte()` — blocking, yields to scheduler if no data.
  - `int BytesAvailable { get; }`
  - `void EnableInterrupts()` / `void DisableInterrupts()`
- Implement **interrupt-driven receive**:
  - Register an IRQ handler for IRQ4 (COM1) with the kernel's interrupt
    framework.
  - On RX interrupt, drain the UART's RBR into a lock-free or
    spinlock-protected ring buffer (size 256 or 1024 bytes).
  - On overrun or line-status error, log a diagnostic to the console and
    clear the error.
  - On TX-empty interrupt, feed the next byte from a TX ring buffer, or
    disable the TX-empty interrupt if the buffer is empty.
- Implement **transmit**:
  - Polled fallback for early boot (before interrupts are enabled).
  - Interrupt-driven path once the scheduler and IDT are up.
  - Block (with scheduler yield) if the TX ring buffer is full.
- Register the driver as a character device at `/dev/ttyS0` with the VFS.
- Expose a `IConsoleDevice` implementation wrapping the driver (see Task 3).

## Task 2 — Line discipline layer

Implement a line discipline between the raw UART driver and the console
abstraction. Requirements:

- **Canonical mode** (default):
  - Echo each printable character back to the terminal as it is typed.
  - Handle `\r` (Enter) as end-of-line; translate to `\n` internally.
  - Handle `0x7F` / `0x08` (Backspace) by erasing the last character in the
    line buffer and emitting `\b \b` to the terminal.
  - Handle `Ctrl+C` (`0x03`): discard the current line buffer, emit `^C\r\n`,
    and signal the waiting reader with a cancellation.
  - Handle `Ctrl+D` (`0x04`): if the line buffer is empty, signal EOF;
    otherwise treat as end-of-line.
  - Handle `Ctrl+U` (`0x15`): clear the line buffer and redraw.
- **Raw mode** (opt-in via API): bytes are passed through to the reader
  without echo or line editing.
- **History** (up to 32 entries, circular):
  - Up arrow (`ESC [ A`) recalls the previous line into the buffer and
    redraws it.
  - Down arrow (`ESC [ B`) recalls the next line or clears the buffer.
  - ANSI escape sequences are parsed in the line discipline, not in the
    caller.
- Expose the line discipline as a `LineDiscipline` class with:
  - `void Feed(byte b)` — called from the UART IRQ handler.
  - `int ReadLine(Span<char> destination)` — blocks until a full line is
    available or cancellation occurs.
  - `void SetMode(LineMode mode)`.

## Task 3 — Console Abstraction Layer (CAL)

Define a console device interface and a multiplexer that routes output to
all registered console devices.

- `IConsoleDevice` interface:
  - `void Write(char c)`
  - `void Write(ReadOnlySpan<char> s)`
  - `bool TryReadKey(out ConsoleKeyInfo key)`
  - `ConsoleKeyInfo ReadKey(bool intercept)`
  - `void Clear()`
  - `void SetCursorPosition(int left, int top)`
  - `(int Left, int Top) GetCursorPosition()`
  - `ConsoleColor ForegroundColor { get; set; }`
  - `ConsoleColor BackgroundColor { get; set; }`
  - `void Flush()`
  - `bool IsInputRedirected { get; }`
  - `bool IsOutputRedirected { get; }`
- `ConsoleMultiplexer`:
  - Holds a list of registered `IConsoleDevice` instances.
  - Routes `Write`/`WriteLine` to ALL registered devices (so boot logs go to
    both serial and, if present, VGA text mode).
  - Routes input from a single designated "active input" device (default:
    `ttyS0`).
  - Supports `Register(IConsoleDevice)`, `Unregister(IConsoleDevice)`, and
    `SetActiveInput(IConsoleDevice)`.
- In Phase 2, exactly one device must be registered: `/dev/ttyS0` (the
  serial console). The VGA text device is deferred to Phase 3; if a stub
  exists from Phase 1, it must NOT be registered by default.

## Task 4 — `System.Console` and related BCL types in `korlib`

Implement (or complete) the following types in the `korlib` runtime library
so that standard C# console code compiles and runs on NeutrinoOS:

- `System.Console`:
  - `Write`, `WriteLine` (all common overloads: `string`, `char`, `int`,
    `long`, `bool`, `object`, and format-string overloads delegating to
    `string.Format`).
  - `Read`, `ReadLine`, `ReadKey()` and `ReadKey(bool intercept)`.
  - `Clear()`.
  - `ForegroundColor` / `BackgroundColor` (map `ConsoleColor` to ANSI SGR
    sequences emitted to the serial console).
  - `ResetColor()`.
  - `SetCursorPosition(int, int)` and `CursorLeft` / `CursorTop` properties
    (implemented via ANSI `ESC [ row ; col H`).
  - `WindowWidth` / `WindowHeight` — return configured terminal size
    (default 80x50; configurable via a boot parameter).
  - `OutputEncoding` / `InputEncoding` — return `Encoding.UTF8` (serial
    transmits bytes; the driver already writes UTF-8 bytes).
  - `IsInputRedirected` / `IsOutputRedirected` — return `false` in Phase 2.
- `System.IO.TextWriter` / `TextReader`:
  - `Console.Out` returns a `TextWriter` that delegates to the CAL.
  - `Console.In` returns a `TextReader` that delegates to the line
    discipline.
  - `Console.Error` returns a second `TextWriter` that writes to the same
    device (Phase 2 does not need to distinguish stdout/stderr at the TTY
    layer; the distinction is preserved at the `Console` API level for
    later redirection).
- `System.IO.TextWriter.Synchronized` and basic buffering:
  - Buffer up to 512 chars and flush on `\n`, on `Flush()`, or on a 50 ms
    timer tick, whichever comes first, to reduce per-character UART writes.
- `System.ConsoleColor` enum with the standard 16 values.
- `System.ConsoleKey` / `ConsoleKeyInfo` / `ConsoleModifiers`:
  - Map incoming bytes (and ANSI escape sequences for arrows, Home, End,
    Delete, PgUp, PgDn, F1–F12) to `ConsoleKeyInfo`.
  - Modifiers: track Ctrl (via `0x01`–`0x1A` mapping) and Shift (best-effort
    via terminal escape sequences).
- `System.Text.Encoding.UTF8`:
  - Provide a minimal `Encoding` base class and a `UTF8Encoding`
    implementation sufficient for `Console.OutputEncoding` and for
    `Encoding.GetBytes(string)` / `Encoding.GetString(byte[])` used by
    console code. Do NOT port the full BCL encoding subsystem.
- `System.Environment`:
  - `Environment.NewLine` returns `"\n"` (the driver translates to `\r\n`).
  - `Environment.Exit(int)` — call the kernel's process-exit syscall.
  - `Environment.ExitCode` property.
  - `Environment.GetCommandLineArgs()` — return the argv captured at launch.
  - `Environment.CurrentDirectory` — return the process CWD from the VFS.

## Task 5 — Kernel integration

- Ensure `System.Console.Write*` calls anywhere in the kernel, drivers, or
  user code route through the CAL and out to `/dev/ttyS0`.
- Replace any remaining direct UART calls in the kernel with CAL calls, so
  the kernel never writes to the UART directly after the CAL is initialized.
- Ensure the boot sequence logs via `System.Console.WriteLine` so that the
  serial console banner from Phase 1 still appears, now driven by the new
  CAL.
- Ensure the kernel exposes a way for user-mode processes (once Phase 6
  lands) to obtain the console handles. In Phase 2, this can be a stub
  syscall that returns the file descriptor of `/dev/ttyS0`.
- Ensure the shell prompt from Phase 1 becomes a `Console.ReadLine()` call
  in the minimal shell, so that the line discipline and history are
  exercised end-to-end.

## Task 6 — Testing and documentation

- Add a `tests/` directory with a small C# test program (`console_io_test.dll`)
  that exercises:
  - `Console.Write` / `WriteLine` with strings, ints, and formatted output.
  - `Console.ReadKey(true)` for single-key reads.
  - `Console.ReadLine()` with backspace, Ctrl+C, Ctrl+D, and arrow-key
    history.
  - ANSI color output (`ForegroundColor = ConsoleColor.Red; WriteLine(...);
    ResetColor();`).
  - `Console.Clear()`.
  - `Console.SetCursorPosition` and cursor position read-back.
- Add a `make run-qemu-serial` target that boots QEMU and pipes the serial
  stream to the WSL2 terminal, and a `make run-qemu-serial-log` target that
  also tees the output to `build/serial.log` for later inspection from
  Windows.
- Add a `scripts/test-console.ps1` PowerShell script for Windows 11 that:
  - Launches QEMU from WSL2 with the serial stream redirected to a named
    pipe or a file.
  - Sends a scripted sequence of keystrokes (via `socat` or by writing to
    the QEMU monitor) to validate line editing and history.
  - Asserts that the expected output appears in the captured log.
- Produce `docs/PHASE2-DESIGN.md` describing:
  - The UART driver's register layout and interrupt flow.
  - The line discipline's state machine (including the ANSI escape parser).
  - The CAL interface and the multiplexer's routing rules.
  - The subset of `System.Console` implemented, with a table of supported
    overloads and known deviations from the full BCL.
- Produce `docs/PHASE2-ACCEPTANCE.md` listing the acceptance criteria below
  and how to verify each.

# CONSTRAINTS

- All code must be C# (plus the existing assembly intrinsics). Do NOT add
  C or C++ files to the kernel, bootloader, or any driver.
- Do NOT introduce a graphical framebuffer, GUI, or window manager.
- Do NOT use the term "TTY" as a project name or suffix. It is fine to use
  the Unix term "tty" in device paths (`/dev/ttyS0`) and in documentation.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to ProtonOS.
- Do NOT scope-creep into Phase 3+ (VGA text mode, networking, JIT, shell
  command parsing, utilities). If a Phase 3+ concern arises, note it in a
  "Deferred to later phases" section of the report.
- Every public type and method added to `korlib` must have XML doc comments
  describing its Phase 2 semantics and any deviations from the official
  .NET BCL.
- All user-visible strings must say "NeutrinoOS".

# DELIVERABLES

1. A production-quality UART 16550 driver for COM1 with interrupt-driven
   RX and TX, registered as `/dev/ttyS0`.
2. A line discipline layer with canonical mode, raw mode, backspace editing,
   Ctrl+C/Ctrl+D/Ctrl+U handling, and 32-entry arrow-key history.
3. A Console Abstraction Layer with `IConsoleDevice` and
   `ConsoleMultiplexer`.
4. A `korlib` implementation of `System.Console`,
   `System.IO.TextWriter`/`TextReader`, `System.ConsoleColor`,
   `System.ConsoleKey`/`ConsoleKeyInfo`/`ConsoleModifiers`,
   `System.Text.Encoding.UTF8`, and `System.Environment` sufficient to run
   a standard .NET 10 console application.
5. `tests/console_io_test.dll` source and a `make` target to build it.
6. `make run-qemu-serial`, `make run-qemu-serial-log`, and
   `scripts/test-console.ps1`.
7. `docs/PHASE2-DESIGN.md`, `docs/PHASE2-ACCEPTANCE.md`, and
   `PHASE2-REPORT.md`.

# ACCEPTANCE CRITERIA

Phase 2 is complete when ALL of the following are true:

- [ ] `make image` and `make run-qemu-serial` still boot to a NeutrinoOS
      banner on the serial console, with no regressions from Phase 1.
- [ ] Typing characters at the `neutrinoos>` prompt echoes them back.
- [ ] Backspace (`0x7F` or `0x08`) erases the last typed character on the
      terminal via `\b \b`.
- [ ] Pressing Enter submits the line, and the minimal shell echoes the
      submitted line back via `Console.WriteLine`.
- [ ] Pressing Ctrl+C clears the current line, prints `^C`, and re-prompts.
- [ ] Pressing Ctrl+D on an empty line signals EOF (the shell prints
      "logout" and exits, or re-prompts depending on configuration).
- [ ] Up arrow recalls the previous line; down arrow recalls the next line
      or clears the buffer, with correct terminal redraw.
- [ ] `Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("x");
      Console.ResetColor();` produces the corresponding ANSI SGR sequence
      on the serial stream (verified by inspecting `build/serial.log` for
      `\x1b[31m` and `\x1b[0m`).
- [ ] `Console.Clear()` emits `\x1b[2J\x1b[H` and subsequent output begins
      at the top-left of the terminal.
- [ ] `Console.SetCursorPosition(10, 5)` emits `\x1b[6;11H` and
      `Console.CursorLeft`/`CursorTop` return the values set.
- [ ] `Console.ReadKey(true)` returns the correct `ConsoleKeyInfo` for
      letters, digits, Enter, Escape, Tab, Backspace, arrows, Home, End,
      Delete, PgUp, PgDn, and F1–F12.
- [ ] `Console.ReadLine()` returns a correctly edited line, including
      history recall, and correctly handles Ctrl+C (returns null or throws
      `OperationCanceledException`, per documented behavior).
- [ ] The UART RX path is interrupt-driven: sending 10 KB of data to the
      guest's serial port without reading it must not drop characters
      (verified by a test that counts bytes received vs. sent).
- [ ] No C or C++ files exist in the kernel, bootloader, or driver
      directories.
- [ ] `docs/BUILD-WINDOWS.md` (from Phase 1) still works, and
      `docs/PHASE2-ACCEPTANCE.md` provides step-by-step verification for
      every checklist item above from a fresh Windows 11 machine.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps mapped to the six tasks
   above.
2. **Repository layout** — the target directory tree after Phase 2,
   highlighting new and modified files.
3. **Code changes** — for each file to be created, modified, or deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created files) OR a unified diff
     (for modifications) OR a precise description (for deletions).
   - For large files (e.g., the full `System.Console` implementation),
     provide the complete source; do not abbreviate with "..." unless the
     omitted region is boilerplate that is explicitly described.
4. **Kernel integration notes** — a short section describing exactly where
   in the Phase 1 kernel init sequence the CAL, UART driver, and line
   discipline are initialized, and in what order.
5. **Build and test commands** — exact WSL2 bash commands and PowerShell
   commands for Windows 11 to build, run, and verify Phase 2.
6. **Acceptance checklist** — reproduce the checklist above, with a one-line
   note for each item explaining how it is satisfied.
7. **Deferred to later phases** — anything that came up that belongs to
   Phase 3+ (VGA text mode, VFS-backed device nodes, syscall plumbing,
   JIT execution, shell parsing, networking, SSH, curl).
8. **Open questions / assumptions** — anything ambiguous about the Phase 1
   output, the existing `korlib` structure, or the ProtonOS conventions
   that you assumed, and how the user can verify or correct them.

If any part of the Phase 1 output is unclear or the existing `korlib`
layout does not match your assumptions, state your assumptions explicitly
and proceed with a reasonable layout consistent with a bflat-based managed
kernel, noting where the user must adjust paths.

Do not skip ahead to Phase 3–8. Scope discipline is mandatory: Phase 2 only.