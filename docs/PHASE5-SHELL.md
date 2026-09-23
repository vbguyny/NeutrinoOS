# Phase 5 - Terminal Shell

NeutrinoOS Phase 5 ships an interactive shell (the "NeutrinoOS console")
that runs on the serial/VGA console, executes external .NET 10 utilities
from the kernel's Tier-0 JIT, and provides the usual POSIX-style
conveniences: pipelines, redirection, variables, background jobs,
history, tab completion and a configurable prompt.

Implementation lives in `src/kernel/Shell/` (kernel AOT, `ProtonOS.Shell`):

| File | Purpose |
|------|---------|
| `ShellLexer.cs` | tokenizer (quotes, escapes, `$VAR`, `$?`, `$$`) |
| `ShellParser.cs` | grammar → `ShellPipeline` (commands, args, redirects, operators) |
| `ShellExecutor.cs` | runs pipelines (builtins, externals, pipes, redirection) |
| `ShellBuiltins.cs` | built-in commands |
| `ShellState.cs` | variables, aliases, PATH lookup, exit state |
| `ShellCompletion.cs` | TAB completion (builtins + `$PATH` + paths) |
| `JobManager.cs` | background job table + cooperative pump |
| `ShellInit.cs` | `/etc/profile`, `/profile`, `/history.txt`, prompt (PS1) |
| `ShellMain.cs` | REPL loop |
| `KernelGc.cs` | the `gc` built-in (mark-only statistics) |

## Grammar

```
pipeline  := command (('|' | '&&' | '||' | '&' | ';')? command)*
command   := word+ redirection*
redirection := ('>' | '>>' | '2>' | '2>>' | '<') word
```

* One pipeline line can contain up to 8 commands, 64 args per command
  and 8 redirections.
* Pipelines and `&&`/`||` chains can be chained left-to-right; `;`
  sequences always continue; `&` backgrounds the command before it.
* Errors report a column number from the lexer/parser.

### Lexing

| Syntax | Meaning |
|--------|---------|
| `'...'` | literal (no escapes) |
| `"..."` | interpolation allowed, backslash escapes |
| `\ ` `\"` `\'` `\\` | escaped character outside quotes |
| `$VAR` | environment/shell variable expansion |
| `$?` | exit code of the last command |
| `$$` | shell PID (1000 for the interactive shell) |
| `#` at word start | comment |

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | success |
| 1 | generic failure (bad builtin usage, redirection failure, ...) |
| 2 | syntax error |
| 126 | found but not executable |
| 127 | command not found |
| 130 | killed (background job cancelled) |
| *rc* | the external program's return value |

## Built-ins

`cd`, `pwd`, `exit`, `logout`, `export`, `unset`, `history`, `alias`,
`unalias`, `source`, `jobs`, `fg`, `bg`, `help`, `run`, `true`, `false`,
`gc` - each prints usage with `<name> --help`.

* `cd` with no argument goes to `$HOME`; `cd -` returns to the previous
  directory (`$OLDPWD`).
* `export NAME=value`, `unset NAME` update the environment table shared
  with the DDK (`Environment.GetEnvironmentVariable` from utilities).
* `history` shows the in-memory list (also `/history.txt` on disk);
  `history N` shows the last N entries.
* `alias name=value` registers an alias used before command lookup.
* `source file` runs a script file through the executor.
* `jobs` prints `[id] pid state (exit code) command`; `fg`/`bg` are
  accepted and documented as no-ops in the cooperative job model
  (Phase 5 has no preemptive processes, see below).
* `gc` runs a mark-only GC pass and prints heap statistics.
* `run` executes an assembly by path with arguments (used by the
  launcher for external commands).

## External commands ($PATH)

Commands that are not built-ins are searched in `$PATH`
(default `/bin:/apps`) as `<name>.dll` and executed by the kernel's
Tier-0 JIT (`AssemblyRunner`). The kernel caches loaded assemblies per
path, so repeated invocations are instant after the first run.
Standard input/output/error connect to the console, the pipeline
buffer, or the redirected file depending on the command line.

Exit codes propagate: `ls /nope` returns 1, and `$?` reports it.

## Redirection

| Form | Effect |
|------|--------|
| `> file` | stdout to file (truncate) |
| `>> file` | stdout to file (append) |
| `2> file` | stderr to file (truncate) |
| `2>> file` | stderr to file (append) |
| `< file` | stdin from file |

Internal pipes are single-shot and in-memory (a `StringWriter` captures
the producer; the consumer reads from a `StringReader`); external
processes are run to completion sequentially. Output is committed when
the producer finishes, matching `cmd > file` semantics for scripts.

## Variables and the environment

The shell keeps its own variable table and mirrors `export`ed values
into the kernel environment table (64 entries) that utilities see via
the DDK. Defaults set at startup:

```
PATH=/bin:/apps  HOME=/  SHELL=/bin/shell.dll  TERM=vt100  USER=root
```

`$PWD` tracks `cd` (kept in sync with the kernel CWD).

## Startup files, prompt and history

* `/etc/profile` is sourced at shell start when present (the deploy
  script installs a sample), then `/profile` (user profile).
* `PS1` controls the prompt; defaults to `neutrinoos> ` (unless
  `/etc/profile` overrides it). Expansions: `\u` user, `\h` host name
  (`NeutrinoOS`), `\w` current directory, `\$` `#` for root, `\n` new
  line, `\\` backslash. Example:
  `export PS1='\u@\h:\w\$ '` → `root@NeutrinoOS:/$ `.
* History is kept in memory during the session (arrow keys up/down) and
  written to `/history.txt` on `exit`/`logout`; it is loaded again on
  the next boot. (FAT short-name rules: no leading-dot files, hence
  `/history.txt` instead of `~/.history`.)

## Tab completion

TAB completes:

* the first word from the built-in list and from `<name>.dll` files in
  each `$PATH` directory (case-insensitive, matching FAT's upper-case
  names; completed command words get a trailing space),
* later words as file system paths (case-insensitive; directories get a
  trailing `/`),
* on ambiguity it extends to the longest common prefix, or lists the
  candidates (up to 32, with a `... (N more)` tail).

The completion runs from the idle poll (20 ms debounce) so a burst of
TABs coalesces and line editing stays responsive. The first completion
after boot pays a one-time JIT cost while the directory helpers compile.

## Background jobs (cooperative)

`cmd &` registers a job (banner: `[jobs] [id] pid NNNN started: cmd`,
PIDs at 1001+) and returns to the prompt immediately. Jobs execute when
the shell is idle waiting for input, one at a time, on the shell thread:
Phase 5 has no preemptive processes, so while a job runs the shell
cannot read new input, and jobs queue behind each other.

* `jobs` lists state (`queued`/`running`/`done`/`killed`) and exit code.
* `kill <id|pid>` cancels a queued job (marked `killed`, exit 130).
  A job that already finished cannot be killed ("no such job").
* Backgrounding a pipeline with pipes/redirections is rejected with a
  clear message (documented limitation).
* `ps` (utility) shows the same job table plus kernel threads.

## Console notes

The shell runs on the serial console and (in GUI images) on the VGA
console; line editing supports backspace, arrows, Home/End, Ctrl+C
(cancel line), Ctrl+L, TAB completion and history navigation. Kernel
diagnostic prints are minimal in normal operation so the console stays
clean.
