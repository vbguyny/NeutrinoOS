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
- `run /apps/p4async.dll`: `Task.Run(...).Result` and `Task.Run(...).Wait()`
  checks pass (partial file; see blockers).

## Blockers (open JIT issues, in priority order)

1. **`async` deep-await hang**: `p4async` completes its first two checks,
   then stops responding; suspected await-continuation path in the
   synchronous Task builder.
2. **`p4cs14` produced no captured output** - needs a re-run with serial
   capture to separate build-time issues from runtime failures.

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

1. Debug the async hang (blocker 1).
2. Re-run `p4cs14`, `p4inter`, `p4multi`, `p4net` with serial capture and
   close out their acceptance items.
3. Add `System.Linq.AsyncEnumerable` stub + small BCL backlog items.
4. Wire `tests/run-phase4-tests.ps1` into the workflow and re-verify the
   full checklist.

Note: item 2 of the earlier list ("unresolved generic `newobj` token")
was fixed - `p4linq` now passes 34/34 checks; see "Verified working".
