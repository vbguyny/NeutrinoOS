# NeutrinoOS Phase 2 — Serial Console Design

Phase 2 replaces the Phase 1 write-only UART path with a production serial
console subsystem and a `System.Console` implementation in `korlib`,
routed through a Console Abstraction Layer (CAL).

```
+------------------+     +----------------+     +-------------------+
| System.Console   | --> | IConsoleDevice | --> | ConsoleMultiplexer|
| (korlib, AOT)    |     | SerialConsole  |     | fan-out + active  |
| TextWriter/Reader|     | Device (ttyS0) |     | input             |
+------------------+     +----------------+     +-------------------+
        |  DllImport("*") exports        |                |
        v                                v                v
+--------------------------+   +-----------------+  +------------------+
| ConsoleExports.cs        |   | LineDiscipline  |  | Uart16550 driver |
| (kernel symbols)         |-->| echo/edit/hist. |->| 0x3F8, IRQ4      |
+--------------------------+   +-----------------+  +------------------+
```

## 1. UART 16550 driver (`src/kernel/Platform/Serial/Uart16550.cs`)

- Base 0x3F8 (COM1; COM2–4 in the port table), 8 data bits, no parity,
  1 stop bit. Baud rate selectable at runtime (9600–115200; divisor =
  115200/baud from the 1.8432 MHz reference).
- FIFOs enabled, 14-byte RX trigger (FCR = 0xC7); DTR/RTS/OUT2 asserted
  (MCR = 0x0B).
- **RX**: interrupt-driven. IRQ4 → vector 36 via `Arch.RegisterHandler`.
  The handler drains RBR into a 1024-byte ring buffer (`RxConsumer` hook
  routes bytes to the line discipline once the CAL is up). Overrun and
  line-status errors are counted (`RxOverruns`, `HardwareOverruns`,
  `LineStatusErrors`).
- **TX**: 256-byte ring + THRE interrupt when interrupts are enabled
  (blocking writes yield); polled THRE fallback during early boot.
- The driver is a kernel component (not a JIT `Drivers.*` assembly): the
  console must work during early boot, before the driver framework loads,
  and the UART IRQ is the system's input source.

## 2. Line discipline (`src/kernel/Platform/Serial/LineDiscipline.cs`)

State machine fed by `Feed(byte)` from the UART IRQ handler:

- **Canonical mode (default)**: prints echo; CR/LF completes a line;
  `0x7F`/`0x08` deletes with `\b \b`; `Ctrl+C` (0x03) echoes `^C`, clears
  the line, queues a *cancelled* result; `Ctrl+D` (0x04) on an empty line
  queues EOF (on a non-empty line it submits); `Ctrl+U` (0x15) clears and
  redraws via the prompt-tail tracker; other control bytes become
  Ctrl+letter `ConsoleKeyInfo`s.
- **History**: 32 entries (fixed byte store), Up/Down browse a saved copy
  of the in-progress line; redraw = CR + blank (prompt+line) + CR + prompt
  + recalled text (prompt tail tracked by the device, not hard-coded).
- **ANSI parser**: states normal → ESC → CSI/SS3; maps arrows, Home, End,
  Insert, Delete, PgUp, PgDn, F1–F12 (CSI `n~`, SS3 `P`–`S`), with
  xterm modifier params (best effort). A lone ESC becomes the Escape key
  after a 50 ms window (polled from the blocking read loops); incomplete
  sequences are dropped after 200 ms.
- **Raw mode** (`SetRawMode(true)`): bytes pass through without echo or
  editing; CR/LF still completes a line.
- **Queues**: completed lines + EOF/cancel for `ReadLine`; decoded keys
  for `ReadKey`. While a `ReadKey` is active (`BeginKeyRead`), keys go to
  the key queue instead of the editor, so reading a key does not pollute
  a subsequent `ReadLine`.
- The line and key stores are fixed-size structs with `fixed` buffers —
  kernel-compiled code must avoid managed array type tokens (the kernel
  runtime has no `LdTokenHelpers`).

## 3. Console Abstraction Layer (`src/kernel/Platform/Consoles/`)

- `IConsoleDevice`: Write(char/span), TryReadKey/ReadKey, Clear,
  Set/GetCursorPosition, SetColors, Flush, Is*Redirected, Window size,
  KeyAvailable, ReadLine, SetRawMode, GetPromptTail.
- `SerialConsoleDevice` ("ttyS0"): UTF-8 encoding (BMP), LF→CRLF, ANSI SGR
  colors, **shadow cursor** (the serial side cannot report position),
  prompt-tail tracking for redraws.
- `ConsoleMultiplexer`: output fan-out to all registered devices; single
  active input. Phase 2 registers exactly one device — the VGA text
  device is deliberately NOT registered (Phase 3).
- `ConsoleDeviceRegistry`: `/dev/ttyS0` name → device (a VFS-backed /dev
  is deferred; the registry is the Phase 2 stopgap).
- `DebugConsole` remains the kernel's logging facade; it delegates to the
  CAL once initialized (UART directly before that).

### Color mapping (deviation)

`ConsoleColor` maps 0–7 → SGR 30–37 / 40–47 (dark) and 8–15 → SGR 90–97 /
100–107 (bright). The Phase 2 brief's example (`ConsoleColor.Red` →
`\x1b[31m`) corresponds to `DarkRed` under this standard mapping; the test
program emits both sequences (31 and 91) plus the reset (0m) so logs
contain `\x1b[31m` and `\x1b[0m` as required.

## 4. korlib (`src/korlib/System/`)

| Type | Implemented | Deviations |
|------|-------------|------------|
| `Console` | Write/WriteLine overloads, Read, ReadLine, ReadKey/KeyAvailable, Clear, colors, cursor, sizes, encodings, redirect flags, Flush, TreatControlCAsInput, SetRawMode | `ReadLine` reports Ctrl+C as `null` + `Console.LastReadLineCanceled` (no `OperationCanceledException`: AOT exception paths are unreliable in the kernel shell). `Title/Beep/CursorVisible` are no-ops. |
| `ConsoleColor/ConsoleKey/ConsoleKeyInfo/ConsoleModifiers` | full enums/struct, BCL values | — |
| `IO.TextWriter/TextReader` | core Write/WriteLine/Read/ReadLine/ReadToEnd, `NewLine` fixed to `\n` | no async, encoding config or format providers |
| `Text.Encoding/UTF8Encoding` | real UTF-8 encode/decode | minimal BCL subset (no code pages/fallback objects) |
| `Environment` | `NewLine` (`\n`), `Exit`, `ExitCode`, `GetCommandLineArgs` (empty), `CurrentDirectory` ("/") | argv and VFS cwd arrive with later phases |

`Console.Out/Error` are buffered (512 chars; flush on `\n`, `Flush()`,
buffer-full, or 50 ms of pending output, lazily checked on write); all
blocking reads flush first, so prompts always appear before input waits.

## 5. Kernel integration order (`Kernel.Main`)

1. `DebugConsole.Init()` → `Uart16550.Initialize(115200)` (polled output).
2. ... usual boot / (optional) test suites ...
3. `Scheduler.EnableScheduling()`.
4. `ConsoleAbstractionLayer.Initialize()`: creates/registers ttyS0, routes
   UART RX into `LineDiscipline.Feed`, enables RX IRQs.
5. Optional `console_io_test.dll` (marker-gated, see report).
6. `ConsoleSession.Run()`: `[SHELL]` banner, prompt, `Console.ReadLine`
   loop (echo submitted line; Ctrl+C re-prompts via the cancel flag;
   Ctrl+D prints `logout` and halts).

Boot markers (root of the FAT image, added with `mcopy`):

| Marker | Effect |
|--------|--------|
| `skip-boot-tests` | skips the full in-boot JIT/syscall suites (fast console cycles) |
| `run-console-test` | runs `console_io_test.dll` after console init |
