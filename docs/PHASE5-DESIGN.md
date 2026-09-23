# Phase 5 - Design Notes

## Shell architecture

```
Serial/VGA console
   │  (LineDiscipline: line editing, history keys, TAB debounce)
   ▼
ShellMain (REPL)
   │  read line ──► ShellLexer (tokens) ──► ShellParser (pipeline AST)
   ▼
ShellExecutor
   ├─ built-in?           → ShellBuiltins (in kernel)
   ├─ "name" in $PATH?    → AssemblyRunner (Tier-0 JIT, .dll from /bin|/apps)
   ├─ pipes               → StringWriter capture → StringReader feed
   ├─ redirection         → Console.SetOut/SetError/SetIn + FileStream
   └─ operators           → ; && || & (JobManager)
```

### AST

`ShellPipeline` holds up to 8 `ShellCommand` entries plus the operators
connecting them; each `ShellCommand` has up to 64 argument words and 8
redirections (`Path`, `Kind` = In/Out/Append/ErrOut/ErrAppend). Tokens
carry a column for error reporting. All tables are fixed-size kernel
arrays - no allocations per line beyond the strings themselves.

### Execution model

* One line at a time, left to right; `&&`/`||` short-circuit on `$?`.
* Pipes are single-shot and in-memory: the producer runs with its
  stdout redirected into a `StringWriter`, the consumer then runs with
  a `StringReader` on that text. This fits the cooperative model (no
  concurrent processes) and the sizes involved (console-era data).
* Redirections open real files through korlib (`File.Create`/`Append`)
  and are committed when the command completes; `2>`/`2>>` redirect
  `Console.Error` the same way.
* Exit codes: syntax 2, not found 127, not executable 126, killed 130,
  otherwise the program's return value. `$?` is updated for each
  command, including built-ins and failed redirections.

### Line discipline integration

`LineDiscipline` (kernel serial layer) owns the edit buffer. On TAB it
sets a pending flag; `PollTimeouts` (called from the blocking read
loops on timer ticks) runs the completer after a 20 ms quiet period,
echoes the appended tail, or redraws after a candidate listing. The
same poll runs the job pump when the shell is idle and no TAB is
pending, and the escape-sequence timeouts (a lone ESC becomes the
Escape key after 50 ms).

## Cooperative jobs

There are no preemptive processes in Phase 5: assembly execution is not
reentrant (Tier-0 JIT caches, GC stack scanning) so a `&` job runs on
the shell thread when it idles, one job per idle window, to completion.
Consequences (documented in PHASE5-SHELL.md): the prompt is
unavailable while a job runs; `kill` cancels queued jobs and reports an
error for finished ones; pipelines cannot be backgrounded. The job
table (8 entries) is kernel-owned and exported for `ps`/`kill`.

## Kernel export bridges

Phase 5 adds two bridge patterns on top of the existing export ABI:

1. **Boot volume stats (df)** - `FileExports.KernelBootVolumeStats`
   calls the AHCI driver's JIT-compiled `GetBootVolumeStats` helper
   (captured as a function pointer like the other `Boot*` helpers),
   exposed as `Kernel_GetBootVolumeStats` and wrapped in the DDK.
2. **Network pump** - `Platform.NetworkBridge` captures the virtio-net
   driver's `TransmitFrame`/`ReceiveFrame` at bind time and invokes
   `GetNetworkStack()` once to materialize the interface. The exports
   `Kernel_NetPresent`/`Kernel_NetTransmit`/`Kernel_NetReceive` are
   always registered; they report unavailability when no NIC is bound.
   The DDK's `NetworkPump` wraps them for utilities, which pump frames
   between the NIC and the shared `NetworkStack`.

Shared static state: the DDK assembly is loaded once (per path) and its
statics (`NetworkManager`, `VFS`, environment) are the live state seen
by drivers, the kernel and utilities alike - this is what allows
`mount`/`umount`/`ifconfig`/`netstat` to read tables the JIT side
populated.

## Console correctness work

Several debug prints from earlier bring-up phases were removed or
gated because they interleaved with line editing and broke scripted
verification: the FAT mount banner, `[FAT OpenDir]`, `[GCHeap] New LOH
region`, `[StrEq] len mismatch`, the `[AsmLoader] sig mismatch` dump
and the `[ResolveMemberRef*]` TypeSpec dumps.

`ShellCompletion` matches FAT's upper-case directory entries
case-insensitively and offers lower-case command names, appends a
trailing space after a completed command word, and falls back to the
common-prefix rule for ambiguous prefixes (e.g. `/hell` matches both
`HELLO1.TXT` and `HELLOAPP.DLL` and extends to `/HELL`).

## Testing infrastructure

`build/p5-*.sh` (local, gitignored) drive QEMU through a serial FIFO:

| Script | Purpose |
|--------|---------|
| `p5-all.sh` | rebuild kernel + boot image, build 32 utilities, deploy to `run.img` |
| `p5-apps-deploy.sh` | utilities-only rebuild + redeploy |
| `p5-session.sh` | general shell/utility session |
| `p5-sysinfo-test.sh` | the nine system utilities, 16 assertions |
| `p5-tab-test.sh` | tab completion, 5 markers |
| `p5-net-test.sh` | NIC session: 15 assertions incl. HTTP servers on the host |

All scripts kill stale QEMU instances before boot (`pkill -f
qemu-system` - note the 15-character task-name truncation) and detect
image-lock failures early.

Published test entry points for users: `tests/run-phase5-tests.ps1`,
`scripts/phase5-demo.sh`, `scripts/phase5-demo.ps1`.
