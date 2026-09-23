# ROLE

You are a senior systems engineer specializing in command-line shell design,
POSIX-like process management, pipe and redirection implementation on bare
metal, and managed runtime utility development in C#. You are assisting in
Phase 5 of a custom operating system project.

# PROJECT CONTEXT

Project name: NeutrinoOS
Base project: ProtonOS (a managed OS written entirely in C# using bflat's
  zero-library mode, with a Tier-0 JIT compiler).

Phase 1 status: COMPLETE. Graphics/framebuffer/GOP removed. Serial console
  at COM1 (0x3F8, 115200 8N1) is the primary output.

Phase 2 status: COMPLETE. Production UART 16550 driver (`/dev/ttyS0`) with
  interrupt-driven RX/TX. Line discipline with canonical/raw modes,
  backspace editing, Ctrl+C/Ctrl+D/Ctrl+U, 32-entry arrow-key history.
  Console Abstraction Layer (CAL) with `IConsoleDevice` and
  `ConsoleMultiplexer`. `korlib` implements `System.Console`,
  `System.IO.TextWriter`/`TextReader`, `System.ConsoleColor`,
  `System.ConsoleKey`/`ConsoleKeyInfo`/`ConsoleModifiers`,
  `System.Text.Encoding.UTF8`, and `System.Environment`.

Phase 3 status: COMPLETE. VGA text-mode driver (`/dev/vga0`) with 80x25 and
  80x50 modes, ANSI parser, CP437 font. PS/2 keyboard driver (IRQ1,
  scancode set 1, key repeat, modifier tracking). CAL routes output to both
  consoles and switches active input automatically.

Phase 4 status: COMPLETE. Tier-0 JIT compiler validates .NET 10 assemblies
  (C# 14). `korlib` expanded with `System.IO`, `System.Collections.Generic`,
  `System.Linq`, `System.Threading`, `System.Threading.Tasks` (synchronous
  minimal), `System.Text`, `System`, `System.Net`, `System.Globalization`,
  `System.Diagnostics`. Cross-assembly loading with `AssemblyLoadContext`.
  Standard .NET 10 console applications can be compiled on Windows 11 with
  `dotnet build -f net10.0` and executed on NeutrinoOS.

Target of the overall project: A console-only, headless managed OS that runs
  .NET 10 console applications and utilities on bare metal, with TCP/IP
  networking, SSH, curl, and the ability to host .NET web apps. No GUI,
  no graphical framebuffer, no window manager.

# ENVIRONMENT

- Host OS: Windows 11 (x64)
- IDE: Visual Studio Code (latest stable) with the Remote - WSL extension
- Shell: PowerShell 7 on the host; bash inside WSL2 Ubuntu 24.04
- Toolchain (already installed in WSL2 from Phase 1):
  - .NET SDK 10.0 (with C# 14)
  - bflat (configured to target .NET 10)
  - clang / ld.lld (LLVM 17+)
  - GNU make
  - Python 3.11+
  - qemu-system-x86_64 with OVMF firmware
  - git
- Phase 4 boot verification used:
  `make run-qemu-vga` (boots to NeutrinoOS shell prompt on both serial and
  VGA consoles). `tests/run-phase4-tests.ps1` verifies eight test
  applications covering Hello World, interactive console, file I/O,
  collections and LINQ, async/await, networking, multi-assembly, and
  C# 14 features.
- Windows 11 host has VirtualBox 7.x installed for manual verification.

# PHASE 5 GOAL — "TERMINAL SHELL AND UTILITIES"

Replace the minimal shell from Phase 2 (which only supported `run
/apps/app.dll` and echoed lines) with a production-quality terminal shell
that supports command parsing, pipes, redirection, background execution,
environment variables, and a comprehensive set of built-in commands.
Ship a set of utilities written in C# and compiled for .NET 10 as managed
assemblies, covering file operations, network operations, process
management, and system diagnostics. Phase 5 is complete when a user can
interact with NeutrinoOS using familiar Unix-like commands (`ls`, `cat`,
`cd`, `mkdir`, `rm`, `cp`, `mv`, `ps`, `kill`, `ifconfig`, `ping`,
`wget`, `curl`, `ssh`, `gc`, `mount`, `df`, etc.) and can compose commands
with pipes (`|`) and redirection (`>`, `>>`, `<`).

Phase 5 does NOT include: a full TCP/IP stack (the ProtonOS stack is used;
if incomplete, document limitations), an SSH *server* (the SSH *client*
is Phase 5; the server is Phase 6), web hosting (Phase 7+), or a full
regex engine (a minimal regex implementation is acceptable, or defer to
Phase 6 and document). It is strictly about the shell, command parsing,
and the utility suite.

# DETAILED TASKS

## Task 1 — Shell command parser

Implement a robust shell command parser in C#. Requirements:

- **Tokenizer**: Split an input line into tokens, handling:
  - Whitespace separation (spaces and tabs).
  - Single quotes (`'...'`) — literal, no escaping.
  - Double quotes (`"..."`) — allow `\"`, `\\`, `\$`, `` \` `` escapes.
  - Backslash escaping outside quotes (`\ `, `\"`, `\'`, `\\`).
  - Variable expansion: `$VAR`, `${VAR}`, `$?` (last exit code), `$$`
    (shell PID).
- **Parser**: Produce an AST (or a flat command list) representing:
  - A pipeline of one or more commands separated by `|`.
  - Each command with: program name, argument list, input redirection
    (`<`), output redirection (`>`, `>>`), error redirection (`2>`, `2>>`).
  - Background execution (`&`) at the end of a pipeline.
  - Sequential execution (`;`) and conditional execution (`&&`, `||`).
- **Grammar**: Support the following subset (document what is not
  supported):
  - `cmd arg1 arg2`
  - `cmd1 | cmd2 | cmd3`
  - `cmd > file`, `cmd >> file`, `cmd < file`
  - `cmd 2> file`, `cmd 2>> file`
  - `cmd1 ; cmd2`
  - `cmd1 && cmd2`, `cmd1 || cmd2`
  - `cmd &` (background)
- **Built-in vs external dispatch**: The parser produces a generic command
  node; the executor decides whether it is a built-in or an external
  `.dll` to be loaded via the Tier-0 JIT.
- **Error reporting**: Produce clear error messages for syntax errors
  (unclosed quote, unexpected `|`, missing redirection target), with the
  column number where the error occurred.
- **Reference**: The `XenoAtom.CommandLine` library is a lightweight,
  NativeAOT-friendly command-line parser for .NET that may serve as
  inspiration for the argument-parsing portion (do not depend on it; it
  is a desktop library, not a bare-metal runtime library).

## Task 2 — Shell executor

Implement the shell executor that takes the parsed command tree and
executes it. Requirements:

- **Built-in dispatch**: A registry of built-in commands (see Task 3).
  Built-ins execute in the shell process without spawning a new process.
- **External dispatch**: A `.dll` path (absolute or resolved via `$PATH`)
  is loaded via the Tier-0 JIT's `AssemblyLoadContext`, its `Main` method
  is invoked with the argument array, and it runs as a child process.
- **Process creation**: Use the NeutrinoOS kernel's process API (from
  Phase 6 of ProtonOS, if available; if not, create a minimal `Process`
  abstraction in `korlib` and document the limitation) to spawn external
  commands.
- **Pipes**: Implement `|` using an in-memory ring buffer or a kernel
  pipe primitive. For Phase 5, a simple in-memory pipe (bounded buffer,
  blocking read/write) is acceptable. Document whether the pipe is
  byte-oriented or line-oriented.
- **Redirection**: Implement `>`, `>>`, `<`, `2>`, `2>>` by opening the
  target file via the VFS and dup2-ing the file descriptor into the
  child's stdin/stdout/stderr.
- **Background execution**: `&` runs the pipeline in a background process.
  The shell prints the PID and returns immediately. A `jobs` built-in
  lists background jobs. A `fg` built-in brings a job to the foreground
  (optional; document if deferred).
- **Exit codes**: Each command returns an exit code. `$?` exposes the
  last exit code. `&&` executes the right side only if the left side
  exits 0; `||` executes the right side only if the left side exits
  non-zero.
- **Signal handling**: Ctrl+C sends SIGINT to the foreground process
  group. The shell itself catches SIGINT and re-prompts. Background
  processes ignore SIGINT by default.

## Task 3 — Built-in commands

Implement the following built-in commands in C# as part of the shell
(or as `korlib` utilities loaded by the shell). Each must have a
`--help` option that prints a usage message.

### File operations
- `cd [dir]` — change directory (with `cd -` for previous directory,
  `cd ~` for home).
- `pwd` — print working directory.
- `ls [-l] [-a] [dir]` — list directory contents, with long format and
  hidden files options.
- `cat [file...]` — concatenate and print files.
- `mkdir [-p] dir...` — create directories, with parents.
- `rm [-r] [-f] file...` — remove files or directories.
- `cp [-r] src dst` — copy files or directories.
- `mv src dst` — move/rename files or directories.
- `touch file...` — create empty files or update timestamps.
- `echo [args...]` — print arguments (with `-n` to suppress newline).
- `head [-n N] [file]` — print first N lines (default 10).
- `tail [-n N] [file]` — print last N lines (default 10).
- `wc [-l] [-w] [-c] [file]` — count lines, words, characters.
- `grep [-i] [-v] pattern [file...]` — search for pattern (minimal
  regex; document limitations).
- `find [path] [-name pattern]` — find files (minimal; document
  limitations).

### Process management
- `ps` — list running processes (PID, name, state, CPU time).
- `kill [-9] pid` — send signal to process.
- `jobs` — list background jobs.
- `fg [job]` — bring job to foreground (optional).
- `bg [job]` — resume job in background (optional).
- `sleep N` — suspend for N seconds (uses kernel timer).

### System information
- `gc` — trigger a manual garbage collection and print statistics.
- `df` — show filesystem usage (total, used, available).
- `mount` — list mounted filesystems.
- `mount device path` — mount a filesystem.
- `umount path` — unmount a filesystem.
- `uname [-a]` — print system information (NeutrinoOS, kernel version,
  architecture).
- `date` — print current date and time.
- `uptime` — print system uptime.
- `free` — print memory usage (total, used, free, GC heap).
- `env` — print environment variables.
- `export VAR=value` — set an environment variable.
- `unset VAR` — remove an environment variable.
- `history` — print command history.

### Network operations (use the NeutrinoOS TCP/IP stack)
- `ifconfig` — show network interface configuration (IP, MAC, MTU).
- `ifconfig eth0 up|down` — bring interface up or down.
- `dhcp` — request a DHCP lease.
- `ping [-c N] host` — ICMP echo request.
- `dns host` — DNS resolution.
- `netstat` — show network connections and listening ports.
- `wget [-O file] url` — download a file via HTTP/1.1.
- `curl [-o file] url` — HTTP client (GET; POST via `-d`).
- `ssh user@host` — SSH client (connect to a remote host). This uses
  wolfSSH or a minimal SSH client implementation; document the
  limitations.

### Shell control
- `exit [code]` — exit the shell.
- `help [command]` — print help for a command.
- `alias name=command` — create an alias (optional; document if deferred).
- `source file` — execute commands from a file (optional).

## Task 4 — Utility suite (C# / .NET 10)

The built-in commands in Task 3 may be implemented as built-ins (compiled
into the shell) or as external `.dll` utilities loaded via the Tier-0 JIT.
The following utilities MUST be implemented as external `.dll` files
compiled for .NET 10, to demonstrate the JIT execution path:

- `ls.dll`, `cat.dll`, `echo.dll`, `mkdir.dll`, `rm.dll`, `cp.dll`,
  `mv.dll`, `wc.dll`, `grep.dll`, `head.dll`, `tail.dll`.
- `ps.dll`, `kill.dll`, `sleep.dll`.
- `df.dll`, `mount.dll`, `umount.dll`, `uname.dll`, `date.dll`,
  `uptime.dll`, `free.dll`, `env.dll`.
- `ifconfig.dll`, `dhcp.dll`, `ping.dll`, `dns.dll`, `netstat.dll`,
  `wget.dll`, `curl.dll`, `ssh.dll`.

Each utility is a standalone .NET 10 console application that:
- Parses its own arguments (use a minimal argument parser; the
  `XenoAtom.CommandLine` library may serve as inspiration).
- Uses `System.Console` for I/O.
- Uses `System.IO` for file operations.
- Uses `System.Net` for network operations.
- Returns an exit code (0 for success, non-zero for failure).
- Has a `--help` option.

The shell resolves utilities by searching `$PATH` (default:
`/bin:/apps`), so `ls` finds `/bin/ls.dll`.

## Task 5 — Shell initialization and environment

- **Startup file**: On shell launch, read and execute `/etc/profile`
  (if it exists) and `~/.profile` (if it exists).
- **Environment variables**: Default environment:
  - `PATH=/bin:/apps`
  - `HOME=/`
  - `PWD=/`
  - `SHELL=/bin/shell.dll`
  - `TERM=vt100`
  - `USER=root`
- **Prompt**: The default prompt is `neutrinoos>` for root and
  `user@neutrinoos:/path>$` for non-root users (if user management is
  implemented; otherwise always root). Support `PS1` environment
  variable for custom prompts, with `\u` (user), `\h` (host),
  `\w` (working directory), `\$` (root/user indicator).
- **History**: Persistent history file at `~/.history` (or `/tmp/.history`
  if home is read-only). Load on startup, append on exit.
- **Tab completion**: Implement basic tab completion for command names
  (from `$PATH`) and file paths. This requires reading directory entries
  via the VFS and integrating with the line discipline from Phase 2.

## Task 6 — Kernel integration

- The shell runs as a user-mode process (Ring 3) loaded via the Tier-0
  JIT. It uses the Linux-compatible syscall ABI from ProtonOS.
- The kernel launches the shell on `/dev/ttyS0` after init. If a VGA
  console is present, a second shell instance may be launched on
  `/dev/vga0` (optional; document if deferred).
- Ensure that the shell's `Console.ReadLine()` calls route through the
  line discipline and CAL from Phases 2 and 3.
- Ensure that `System.Diagnostics.Process` (or the minimal `Process`
  abstraction) correctly spawns child processes and waits for them.
- Ensure that pipes and redirection use the VFS and the kernel's pipe
  primitive (or the in-memory pipe implementation).

## Task 7 — Testing and documentation

- **Test suite**: Create `tests/run-phase5-tests.ps1` (PowerShell for
  Windows 11) that:
  - Boots NeutrinoOS in QEMU with serial output captured to a log file.
  - Executes a scripted sequence of shell commands via the serial
    console (using `socat` or QEMU monitor `sendkey`).
  - Asserts that the expected output appears in the log.
  - Tests: `ls`, `cat`, `echo`, `mkdir`, `rm`, `cp`, `mv`, `cd`, `pwd`,
    pipes (`ls | wc -l`), redirection (`echo hello > test.txt` then
    `cat test.txt`), background execution (`sleep 2 &` then `jobs`),
    `ifconfig`, `ping -c 1 127.0.0.1`, `wget` against a local HTTP
    server, `curl` against a local HTTP server, `gc`, `df`, `free`,
    `uname`, `date`, `uptime`, `env`, `export`, `unset`, `history`.
- **Sample scripts**: Provide `scripts/phase5-demo.sh` that runs a
  sequence of shell commands demonstrating the shell's capabilities.
  Also provide `scripts/phase5-demo.ps1` for Windows 11.
- **Documentation**:
  - `docs/PHASE5-SHELL.md` — shell grammar, built-in commands, exit
    codes, environment variables, prompt customization.
  - `docs/PHASE5-UTILITIES.md` — list of utilities, their usage, and
    their `--help` output.
  - `docs/PHASE5-DESIGN.md` — parser AST, executor architecture, pipe
    and redirection implementation, process abstraction.
  - `docs/PHASE5-ACCEPTANCE.md` — step-by-step verification for every
    acceptance criterion below from a fresh Windows 11 machine.
  - `PHASE5-REPORT.md` — summary of changes, blockers, deviations.

# CONSTRAINTS

- All code must be C# (plus the existing assembly intrinsics). Do NOT add
  C or C++ files to the kernel, bootloader, drivers, `korlib`, shell, or
  utilities.
- Do NOT introduce a graphical framebuffer, GUI, mouse support, or a
  window manager. This phase is strictly console-only.
- Do NOT implement a full TCP/IP stack (that is out of scope for Phase 5;
  use the existing NeutrinoOS TCP/IP stack from ProtonOS). If it is
  incomplete, document the limitation and implement only the minimum
  needed for the network utility tests.
- Do NOT implement an SSH *server* (that is Phase 6). The SSH *client*
  is in scope for Phase 5.
- Do NOT implement web hosting (that is Phase 7+).
- Do NOT use the term "TTY" as a project name or suffix. It is fine to
  use the Unix term "tty" in device paths (`/dev/ttyS0`) and
  documentation.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to ProtonOS.
- Do NOT scope-creep into Phase 6+ (SSH server, full TCP/IP stack, web
  hosting, full regex engine). If a Phase 6+ concern arises, note it in
  the "Deferred to later phases" section.
- Every public type and method added to the shell, `korlib`, or
  utilities must have XML doc comments describing its Phase 5 semantics.
- All user-visible strings must say "NeutrinoOS".

# DELIVERABLES

1. A production-quality shell command parser with tokenizer, AST
   (or flat command list), pipe/redirection/background grammar, and
   clear error messages.
2. A shell executor with built-in dispatch, external `.dll` dispatch
   via the Tier-0 JIT, pipe and redirection implementation, background
   execution, and `$?` exit code tracking.
3. Built-in commands as specified in Task 3.
4. External utilities (`.dll`, .NET 10) as specified in Task 4, including
   `ls`, `cat`, `echo`, `mkdir`, `rm`, `cp`, `mv`, `wc`, `grep`, `head`,
   `tail`, `ps`, `kill`, `sleep`, `df`, `mount`, `umount`, `uname`,
   `date`, `uptime`, `free`, `env`, `ifconfig`, `dhcp`, `ping`, `dns`,
   `netstat`, `wget`, `curl`, `ssh`.
5. Shell initialization with `/etc/profile`, `~/.profile`, environment
   variables, `PS1` prompt customization, persistent history, and basic
   tab completion.
6. `tests/run-phase5-tests.ps1`, `scripts/phase5-demo.sh`,
   `scripts/phase5-demo.ps1`.
7. `docs/PHASE5-SHELL.md`, `docs/PHASE5-UTILITIES.md`,
   `docs/PHASE5-DESIGN.md`, `docs/PHASE5-ACCEPTANCE.md`,
   `PHASE5-REPORT.md`.

# ACCEPTANCE CRITERIA

Phase 5 is complete when ALL of the following are true:

- [ ] `make image` and `make run-qemu-vga` still boot to a NeutrinoOS
      banner on both consoles, with no regressions from Phase 4.
- [ ] The shell prompt `neutrinoos>` appears on both serial and VGA
      consoles.
- [ ] `ls`, `cat`, `echo`, `mkdir`, `rm`, `cp`, `mv`, `cd`, `pwd` all
      work as expected from the serial console.
- [ ] Pipes work: `ls | wc -l` prints the number of directory entries.
- [ ] Redirection works: `echo hello > test.txt` creates the file, and
      `cat test.txt` prints `hello`.
- [ ] Append redirection works: `echo world >> test.txt` appends to the
      file, and `cat test.txt` prints both lines.
- [ ] Input redirection works: `wc -l < test.txt` counts the lines.
- [ ] Background execution works: `sleep 2 &` returns immediately, and
      `jobs` lists the background job.
- [ ] `ifconfig` shows the network interface (eth0) with IP, MAC, MTU.
- [ ] `ping -c 1 127.0.0.1` sends an ICMP echo and prints a reply.
- [ ] `wget` downloads a file from a local HTTP server.
- [ ] `curl` fetches a URL from a local HTTP server.
- [ ] `ssh` connects to a local SSH server (or prints a clear error if
      no server is available; document the limitation).
- [ ] `gc` triggers a garbage collection and prints statistics (heap
      size, collection count, collection duration).
- [ ] `df` shows filesystem usage (total, used, available).
- [ ] `free` shows memory usage (total, used, free, GC heap).
- [ ] `uname -a` prints `NeutrinoOS` and the kernel version.
- [ ] `date` prints the current date and time.
- [ ] `uptime` prints the system uptime.
- [ ] `env` prints environment variables; `export VAR=value` sets one;
      `unset VAR` removes one.
- [ ] `history` prints the command history.
- [ ] Tab completion completes command names and file paths.
- [ ] `PS1` customization works (e.g., `export PS1='\u@\h:\w\$ '`
      changes the prompt).
- [ ] All external utilities are compiled for .NET 10 and executed via
      the Tier-0 JIT.
- [ ] No C or C++ files exist in the kernel, bootloader, driver,
      `korlib`, shell, or utility directories.
- [ ] `docs/BUILD-WINDOWS.md` (from Phase 1) and
      `docs/PHASE2-ACCEPTANCE.md` / `docs/PHASE3-ACCEPTANCE.md` /
      `docs/PHASE4-ACCEPTANCE.md` still work, and
      `docs/PHASE5-ACCEPTANCE.md` provides step-by-step verification
      for every checklist item above from a fresh Windows 11 machine.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps mapped to the seven
   tasks above.
2. **Repository layout** — the target directory tree after Phase 5,
   highlighting new and modified files.
3. **Code changes** — for each file to be created, modified, or
   deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created files) OR a unified
     diff (for modifications) OR a precise description (for deletions).
   - For large files (e.g., the full shell parser, the executor, the
     utilities), provide the complete source; do not abbreviate with
     "..." unless the omitted region is boilerplate that is explicitly
     described.
4. **Shell grammar** — a BNF or EBNF grammar for the supported shell
   syntax, with a table of what is and is not supported.
5. **Built-in command table** — a table listing each built-in command,
   its syntax, its options, and its exit codes.
6. **Utility table** — a table listing each external utility, its
   source file, its `--help` output, and its exit codes.
7. **Build and test commands** — exact WSL2 bash commands and
   PowerShell commands for Windows 11 to build, run, and verify
   Phase 5.
8. **Acceptance checklist** — reproduce the checklist above, with a
   one-line note for each item explaining how it is satisfied.
9. **Deferred to later phases** — anything that came up that belongs
   to Phase 6+ (SSH server, full TCP/IP stack, web hosting, full
   regex engine, full user management, multi-user support).
10. **Open questions / assumptions** — anything ambiguous about the
    Phase 4 output, the existing Tier-0 JIT compiler, the existing
    `korlib` structure, the existing ProtonOS process API, or the
    ProtonOS conventions that you assumed, and how the user can
    verify or correct them.

If any part of the Phase 4 output is unclear, or if the existing
Tier-0 JIT compiler, `korlib` layout, or ProtonOS process API does not
match your assumptions, state your assumptions explicitly and proceed
with a reasonable layout consistent with a bflat-based managed kernel,
noting where the user must adjust paths.

Do not skip ahead to Phase 6–8. Scope discipline is mandatory:
Phase 5 only.