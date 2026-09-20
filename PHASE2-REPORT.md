# NeutrinoOS Phase 2 Report — Serial Console

Status: **implemented and verified at the shell level**; one JIT-bridge item
remains for the JIT-app test binary (details in §5).

All work is committed on `main` (see the `phase2:` commits). Verification
was performed in WSL2 Ubuntu 24.04 with QEMU/KVM + OVMF on the standard
q35 configuration.

## 1. What was delivered

1. **UART 16550 driver** (`src/kernel/Platform/Serial/Uart16550.cs`):
   runtime-selectable baud (9600–115200), 8N1, 14-byte FIFO trigger,
   DTR/RTS, interrupt-driven RX (IRQ4 → vector 36) into a 1024-byte ring,
   TX ring with THRE interrupts + polled fallback, overrun/error counters.
2. **Line discipline** (`LineDiscipline.cs`): canonical/raw modes, echo,
   backspace (`\b \b`), Ctrl+C/Ctrl+D/Ctrl+U, 32-entry history with full
   redraw, ANSI/SS3 parser (arrows, Home/End/Ins/Del/PgUp/PgDn/F1–F12,
   modifiers best-effort), lone-ESC timeout, line/key queues.
3. **Console Abstraction Layer**: `IConsoleDevice`,
   `SerialConsoleDevice` (UTF-8, LF→CRLF, ANSI SGR colors, shadow cursor,
   prompt-tail tracking), `ConsoleMultiplexer` (fan-out output, single
   active input), `ConsoleDeviceRegistry` (`/dev/ttyS0`),
   `ConsoleAbstractionLayer` bootstrap; `DebugConsole` is now a facade.
4. **korlib**: `System.Console` (full Phase 2 surface), `ConsoleColor`,
   `ConsoleKey/ConsoleKeyInfo/ConsoleModifiers`, `IO.TextWriter/TextReader`
   (buffered writer: 512 chars, flush on `\n`/Flush/full/50 ms),
   `Text.Encoding/UTF8Encoding`, `Environment` additions
   (`Exit/ExitCode/GetCommandLineArgs/CurrentDirectory`).
5. **Shell**: `ConsoleSession` now uses `Console.ReadLine`/`WriteLine`
   (echo of the submitted line, Ctrl+C re-prompt, Ctrl+D `logout`).
6. **Tooling**: `tests/ConsoleIoTest` → `console_io_test.dll`;
   `make consoletest`, `make run-qemu-serial`, `make run-qemu-serial-log`;
   `scripts/test-console.ps1` + `build/wsl-conio-runner.py` (scripted
   keystrokes, log asserts, stall detection, bounded timeouts); boot
   markers `skip-boot-tests` and `run-console-test`.
7. **Docs**: `docs/PHASE2-DESIGN.md`, `docs/PHASE2-ACCEPTANCE.md`, this
   report.

## 2. Boot-critical fixes made along the way (pre-existing defects)

Found while unblocking the console; all committed with analysis:

1. **Tier-0 JIT stack misalignment** (driver-JIT GP fault from Phase 1):
   JIT-emitted dispatch calls could enter AOT code with RSP ≡ 8 (mod 16);
   fixed with alignment shims for all JIT-called helpers + exception paths
   (`native.asm`).
2. **Boot thread destroyed by the ring-3 test process**: `exit(0)` called
   `Scheduler.ExitThread` on the boot thread; kernel never reached the
   console. Fixed with a setjmp-style `kernel_context_save/restore`
   (`native.asm` + `InitProcess`), resume RIP/RSP stored explicitly.
3. **Unhandled IRQ vectors never EOI'd** (`Arch.DefaultHandler`) → a stray
   IRQ2 left vector 34 in service forever, blocking the timer; the first
   `HLT`-based console wait froze. Fixed by EOI-ing unhandled vectors and
   masking the meaningless IRQ2 cascade entry.
4. **Pre-heap allocation in a static cctor** (Phase 2 self-inflicted):
   `ConsoleAbstractionLayer`'s static initializer ran on the first
   `DebugConsole` write (before the kernel heap) and allocated the
   multiplexer → `AllocZeroed` → `FailFast` → silent boot halt. Fixed by
   lazy creation.
5. **Kernel-compiled code cannot use array type tokens**
   (`LdTokenHelpers` absent): static port/IRQ tables and the console
   queues' managed arrays were replaced with switch helpers and fixed
   primitive buffers.

## 3. Verification performed

| Test | Command (WSL) | Result |
|------|---------------|--------|
| Full boot + Phase 1 checks | `python3 build/wsl-boot-test2.py --minimal` | **PASS** (banner, boot, console, shell, prompt, echo, no crash) |
| Fast console acceptance | `timeout 300 python3 build/wsl-conio-runner.py` | **PASS** — prompt, line echo, backspace (`ab␡z`→`az`), Enter submit, history recall (Up), Ctrl+C (`^C`+re-prompt, still alive), Ctrl+D (`logout`) |
| Console test binary | `--with-jit-test` | compiles; execution blocked (see §5) |

The acceptance runner is hermetic (deletes stale markers first), prints
progress every 15 s, aborts on 75 s of silence, and runs under `timeout`
(≤300 s) — full cycle ≈ 90–120 s.

## 4. Deviations (documented in the design doc)

- Ctrl+C → `Console.ReadLine` returns `null` with
  `Console.LastReadLineCanceled == true` (BCL alternative:
  `OperationCanceledException`; not thrown because AOT exception unwinding
  is unreliable in the kernel shell path).
- Colors: standard 16-color mapping (dark = SGR 30–37 / bright = 90–97);
  `DarkRed` → `\x1b[31m`, `Red` → `\x1b[91m`.
- Cursor position is a shadow model (`CursorLeft/Top` return the values
  set); `WindowWidth/Height` = configured 80×50.
- `Console.Read()` uses the key path; `Environment.GetCommandLineArgs` is
  empty and `CurrentDirectory` is `/` until the VFS phase.
- The UART driver is a kernel component rather than a JIT `Drivers.*`
  assembly: the console must exist before the driver framework and the
  UART IRQ feeds system input (rationale in `PHASE2-DESIGN.md`).

## 5. Remaining item — JIT → System.Console AOT bridge

`console_io_test.dll` (a JIT-compiled .NET 10 console app) now compiles:
its `System.Console`/`Encoding`/`Environment` assembly references resolve
to korlib via the loader's virtual-assembly mapping, and
`String.Concat(4 args)` was registered in `AotMethodRegistry`. At
**execution**, however, `Console.WriteLine` binds to korlib's IL stub
(`PlatformNotSupportedException`) instead of the AOT implementation,
because `AotMethodRegistry` has no `System.Console` entries; the kernel's
runtime-EH then reports:

```
[EH] FATAL: Unhandled managed exception!  [EH] Type: System.PlatformNotSupportedException
```

**Next step (well-scoped):** register the console surface in
`AotMethodRegistry.RegisterConsoleMethods()` the same way
`RegisterStringMethods()` does —
`Write/WriteLine` (string/char/int/long/bool/object, signature hashes via
`ComputeSignatureHash`), `ReadLine`, `ReadKey` (needs a struct-return
registration path for `ConsoleKeyInfo`), `Clear`, `SetCursorPosition`,
cursor/color/size properties, plus `Encoding.GetBytes/GetString` and
`Environment` getters — then flip the test to `--with-jit-test` in the
default acceptance run.

## 6. Deferred to later phases

VGA text console device, VFS-backed `/dev` nodes and console fd plumbing,
argv/CWD for processes, shell command parsing/utilities,
`ReadLine`'s full-line mid-cursor editing (Left/Right/Home/End within the
line buffer), networking/SSH/curl, JIT execution of user assemblies
beyond the test harness.
