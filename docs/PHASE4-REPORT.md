# PHASE4-REPORT.md - .NET 10 JIT validation and BCL expansion (status report)

Phase 4 goal: load and execute .NET 10 assemblies (C# 14) from the
NeutrinoOS shell, with console I/O, file I/O and networking through the
runtime. This report summarizes what was delivered, what works, what is
blocked, and what remains.

## Delivered in this drop

1. **Shell `run` command + assembly runner.**
   `Platform.AssemblyRunner` + `TestSupport.ShellRunSupport` execute
   assemblies from the boot FAT volume (`/apps/*.dll`), with `Main`
   argument materialization performed in JIT-compiled frames (GC-safe).
   Verified: `run /apps/hello.dll` and `run /apps/p4hello.dll` print
   `Hello, NeutrinoOS!` and report `[run] exited with code 42` / `0`.
2. **Standard app model.** Applications build with the stock .NET 10 SDK
   (no `NoStdLib`); `System.Runtime`/`System.Console`/`System.Linq`/...
   resolve to korlib at load time (virtual assembly redirect, extended
   with `System.Linq`).
3. **`System.Linq` for korlib** (Enumerable subset, deferred execution,
   stable ordering, GroupBy/Join; see PHASE4-BCL.md).
4. **`System.IO` for korlib** (`File`, `Directory`, `Path`, `FileStream`,
   `MemoryStream`, `StreamReader`, `StreamWriter`, IO exceptions) on top
   of a new kernel file bridge (`Platform.FileExports` ->
   `AhciEntry.WriteBootFile/DeleteBootFile/BootPathExists/ListBootDirEntry`
   + the read helpers), all bridged by method token like the console.
5. **Cross-assembly loading (Task 4 partial).** On-demand `/lib/<name>.dll`
   resolution and load in `AssemblyLoader.ResolveAssemblyRef`, reusing
   the file bridge, with a JIT-compile reentrancy guard.
6. **JIT call-alignment fixes** (see PHASE4-JIT-COMPAT.md §"JIT changes").
   These fixed a deterministic full-boot crash (the `newobj` reserve
   parity bug) and hardened the stack-args call frames.
7. **Eight Phase 4 test applications** under `tests/phase4/` (hello,
   interactive, file I/O, LINQ, async, networking, multi-assembly,
   C# 14 features) + `build/p4-apps-build.sh`, `build/p4-deploy.sh`,
   `build/p4-session.sh` for scripted serial sessions.
8. **Template and tooling (Task 5 partial).**
   `templates/NeutrinoConsoleApp/` (csproj + Program.cs + README) and
   `scripts/build-app.ps1` (build + stage + deploy instructions).
9. **Docs.** PHASE4-JIT-COMPAT.md, PHASE4-BCL.md, PHASE4-DESIGN.md,
   PHASE4-ACCEPTANCE.md, this report.

## Verified working (on QEMU, AHCI harness)

- Full boot with boot tests: `HALTED=0`, boot tests complete, AppTest
  results identical to the pre-Phase-4 baseline (no regressions from the
  alignment fixes, korlib additions, or loader changes).
- `run /apps/hello.dll` / `run /apps/p4hello.dll`: assembly load, JIT
  compile of `Main`, console output through the JIT console bridge,
  `[run] exited with code ...` reporting, `run /nope.dll` graceful error.
- `run /apps/p4linq.dll`: all 34 checks pass (`[linq] PASS`, exit 0) -
  collections (List/Dictionary/HashSet/Queue/Stack) and the korlib LINQ
  operators (Where/Select/SelectMany/OrderBy/ThenBy/GroupBy/Join/...).
  This closes the "generic `newobj` resolution" blocker; the en route
  JIT fixes (array type-argument signatures, GenParamCount skip,
  MemberRef-backed MethodSpec recompilation, vtable-slot registration
  for implicit interface implementations, `constrained.`/`unbox`
  reference-type semantics, interface-dispatch fallbacks) are documented
  in PHASE4-JIT-COMPAT.md.
- `run /apps/p4fileio.dll`: all 16 checks pass (`[fileio] PASS`, exit 0,
  no `RAWV`/`FATAL` lines) - text/byte round trips, append (multi-line
  read-back), `FileStream` + `StreamWriter`/`StreamReader`, `Path`
  helpers, `/apps` enumeration and delete. This closes the file I/O
  blocker (three root causes: FAT directory entry index, vtable slot
  registration numbering, interface-dispatch stub alignment; see
  PHASE4-JIT-COMPAT.md items 22-25 and PHASE4-ACCEPTANCE.md item 5).
- `run /apps/p4async.dll`: all six checks pass (`[async] PASS`, exit 0)
  including `await Task.Delay`, chained awaits via `Task<int>`,
  `Task.FromResult` and async completion. Fixes: `constrained.` calls
  on state-machine structs no longer box, type-arg contexts survive
  nested compiles, and generic-method instantiations are verified per
  instantiation (PHASE4-JIT-COMPAT.md items 27-29).
- `run /apps/p4cs14.dll`: exit 0 (all C# 14 feature checks pass).
  Fixed by the inherited-method base-chain resolution (PHASE4-JIT-COMPAT.md
  item 26); see PHASE4-ACCEPTANCE.md item 10.

## Blockers (open JIT issues, in priority order)

None blocking the Phase 4 acceptance items. Two former blockers were
fixed:

1. **`async` deep-await hang**: `constrained.` callvirt on the state
   machine boxed the struct (mutations lost; `.Result` spun in
   `Task.SpinWait`), and `Start<TStateMachine>` instantiations for
   different state-machine types shared one compiled native. Fixed via
   PHASE4-JIT-COMPAT.md items 27-29.
2. **`p4cs14` produced no captured output**: `Exception.GetType`
   MemberRef (declared on `System.Object`) failed to resolve, so
   `Main`'s codegen failed before any test output. Fixed via item 26.

Remaining (non-blocking) JIT gap: asynchronous suspension - the
synchronous Task model never exercises a deferred continuation path
(see PHASE4-JIT-COMPAT.md "Known open JIT issues").

## Deviations and limitations (documented, by design or discovery)

- `yield return` cannot compile against korlib-as-corlib (shadowed
  iterator interfaces); apps should use hand-written enumerators. korlib
  itself follows this rule (LINQ).
- `Span<T>`/`ReadOnlySpan<T>` are not in the IL BCL yet (kernel AOT has
  them); implicit span conversions are therefore compile-blocked for apps.
- `Task` is synchronous (single-threaded, inline continuations) - see
  PHASE4-DESIGN.md §3.
- `FileStream` buffers whole files (bridge transfers whole files).
- FAT path handling: 8.3 short names only (LFN lookup not implemented in
  the driver). Deploy assemblies with short names.
- `Directory` returns short (upper-case) names from enumeration; no
  wildcards/recursion.
- The boot-time FAT driver self-test still reports
  `File still exists after delete!` (pre-existing; unrelated to Phase 4,
  noted for the drive team).

## Deferred to later phases

- Remaining BCL items: `Random`, `Convert`, `Diagnostics.Stopwatch/Debug`,
  `Text.RegularExpressions`, `System.Net` (use `ProtonOS.Net` today),
  `System.Linq.AsyncEnumerable` + IAsyncEnumerable, full globalization,
  Span in the IL BCL.
- Real task scheduler and true async networking (Phase 5/6).
- Open-handle streaming bridge for `FileStream` (later).
- Shell command parsing, utilities, SSH/curl/web hosting (Phase 5+).
- `docs/BUILD-WINDOWS.md` section for app development (template README and
  `scripts/build-app.ps1` cover it today).

## Next steps (recommended order)

1. Add `System.Linq.AsyncEnumerable` stub + small BCL backlog items.
2. Wire `tests/run-phase4-tests.ps1` into the workflow and re-verify the
   full checklist.
3. Deferred: true asynchronous suspension (task scheduler), `Span<T>` in
   the IL BCL (see "Deferred to later phases").

Note: the earlier blockers (async hang, `p4cs14` no output) were fixed -
see "Verified working" above; `p4linq` (34/34), `p4fileio` (16/16),
`p4cs14` (exit 0), `p4async` (6/6) and `p4multi` (exit 0) all pass, and
the boot gate is unchanged (HALTED=0, boot complete, RAWV=6,
AppTest 20 passed / 4 failed).
