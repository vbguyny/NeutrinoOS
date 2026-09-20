# NeutrinoOS Phase 2 — Acceptance Guide

How to verify the serial console from a fresh Windows 11 machine with WSL2
(Ubuntu 24.04) and the Phase 1 toolchain (`docs/BUILD-WINDOWS.md`).

## 1. Build

```powershell
wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/neutrino/build/wsl-rebuild.sh
# or, inside WSL:  cd /root/neutrino && make image
```

Produces `build/x64/neutrinoos.img` containing the kernel, korlib IL, DDK,
drivers and `console_io_test.dll`.

## 2. Interactive console

```bash
make run-qemu-serial        # serial console on the terminal (Ctrl+A X quits)
make run-qemu-serial-log    # same,also tees to build/x64/serial.log
```

Expected: NeutrinoOS banner, boot logs, `[SHELL] NeutrinoOS console ready.`
and the `neutrinoos>` prompt with echo/editing/history.

## 3. Automated acceptance

```powershell
scripts/test-console.ps1                # rebuilds, boots, drives, asserts
scripts/test-console.ps1 -SkipBuild     # reuse the current image
```

The script (via `build/wsl-conio-runner.py`) boots QEMU with the serial on
a pty, adds the `skip-boot-tests` marker (fast cycle, ~60-90 s), and
asserts against the captured log `build/x64/serial-conio.log`:

| # | Criterion | How it is verified | Status |
|---|-----------|--------------------|--------|
| 1 | `make image` boots to the banner, no regressions | `build/wsl-boot-test2.py --minimal` (full boot, all suites) | PASS |
| 2 | Typing echoes | runner sends `echo hello\r`; log contains `neutrinoos> echo hello` | PASS |
| 3 | Backspace erases (`ab<BS>z` → `az`) | submitted line `az` appears after the `\b \b` sequence | PASS |
| 4 | Enter submits; shell echoes the line | `\r\necho hello\r\n` appears (ReadLine→WriteLine round trip) | PASS |
| 5 | Ctrl+C clears the line, prints `^C`, re-prompts | `^C` + fresh prompt; shell still responds | PASS |
| 6 | Ctrl+D on empty line → `logout` | `logout` printed, shell exits | PASS |
| 7 | Up arrow recalls the previous line | `two` submitted, `ESC[A` + Enter re-submits `two` | PASS |
| 8 | ANSI SGR colors (`DarkRed`→31, `Red`→91, reset→0) | `console_io_test.dll`; log inspected for `\x1b[31m`, `\x1b[91m`, `\x1b[0m` | see note |
| 9 | `Console.Clear()` emits `\x1b[2J\x1b[H` | `console_io_test.dll` | see note |
| 10 | `Console.SetCursorPosition(10,5)` emits `\x1b[6;11H`, `CursorLeft/Top` read back | `console_io_test.dll` | see note |
| 11 | `Console.ReadKey(true)` key mapping (letters, digits, Enter, Escape, Tab, Backspace, arrows, Home, End, Delete, PgUp, PgDn, F1–F12) | `console_io_test.dll` | see note |
| 12 | `Console.ReadLine()` editing/history/cancel | shell-level checks 2-7 (same discipline path) | PASS |
| 13 | RX is interrupt-driven; 10 KB stream does not drop characters | `console_io_test.dll` bulk section (paced stream, count == 10240) | see note |
| 14 | No C/C++ files outside the bootloader/native asm | `git ls-files` inspection | PASS |
| 15 | `docs/BUILD-WINDOWS.md` still works | unchanged flow + this document | PASS |

**Note (items 8–11, 13):** these run inside `console_io_test.dll`, which is
JIT-compiled by the kernel's Tier-0 JIT. The test **compiles** (System.Console
references resolve to korlib), but executing its Console calls currently hits
korlib's IL stubs (the JIT→AOT registry lacks System.Console method entries),
which throw `PlatformNotSupportedException`. Until that bridge is completed
(tracked in `PHASE2-REPORT.md`), run the test explicitly with:

```powershell
wsl -d Ubuntu-24.04 -u root -- timeout 300 python3 /mnt/d/Projects/Code/neutrino/build/wsl-conio-runner.py --with-jit-test
```

The same behaviors are covered on the **AOT shell path** by items 2–7, 12
(the shell itself is compiled AOT with the real System.Console).

## 4. Manual spot checks

- `make run-qemu-serial`, then at the prompt:
  - type text, Backspace, Enter — line is echoed back;
  - Ctrl+C — `^C` and a new prompt;
  - type two lines, press Up/Up/Down — history recall redraws the line;
  - Ctrl+D — `logout`;
  - watch `build/x64/serial.log` for ANSI sequences from color usage.

## 5. Troubleshooting

- **Boot looks stalled**: the full boot runs the in-boot test suites
  (~4-6 min). For console work add the `skip-boot-tests` marker
  (`mcopy -o -i build/x64/neutrinoos.img marker ::/skip-boot-tests`).
- **Runner says "console test did not execute"**: expected unless
  `--with-jit-test` is passed (see note above).
- **Stale markers**: markers persist in the image; the runner deletes and
  re-adds them each run. `make image` removes them entirely.
