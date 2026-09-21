# PHASE4-JIT-COMPAT.md - .NET 10 / C# 14 compatibility of the Tier-0 JIT

Phase 4 audit of the NeutrinoOS Tier-0 JIT against .NET 10 assemblies and
C# 14 codegen, with the changes made and the gaps that remain. Everything
below was verified empirically on the Phase 4 test applications
(`tests/phase4/`) unless marked otherwise.

## Assembly and metadata format

| Area | Result |
|------|--------|
| .NET 10 metadata / IL version accepted | YES - the loader opens assemblies built with SDK 10.0.401 (`p4hello`, `p4linq`, `p4cs14`, `p4async`, `p4fileio`, `p4multi`, `ProtonOS.Net`, driver DLLs) without version patches. |
| Standard BCL-compiled apps (no `NoStdLib` hack) | YES - applications compile against the normal net10.0 reference assemblies; AssemblyRefs to `System.Runtime`, `System.Console`, `System.Linq` (added in Phase 4), `System.Collections`, `System.Threading*`, `System.Runtime.Extensions`, `System.Text.Encoding` and `netstandard` resolve to korlib at load time (`AssemblyLoader.IsVirtualAssembly`). |
| Entry point invocation | YES - `run /apps/<name>.dll` locates `Main()` (void/int, optional `string[]`), materializes `args` in JIT-compiled frames, and reports `[run] exited with code N`. |
| Cross-assembly references | YES (Phase 4) - an AssemblyRef that is not already loaded is resolved on demand from `/lib/<Name>.dll` through the kernel file bridge (`AssemblyLoader.TryLoadAssemblyFromLib`). |
| FAT file-name constraint | LIMITATION - the FAT driver matches 8.3 short names only (no LFN lookup); deploy `<= 8`-character assembly names (`p4hello`, not `phase4hello`). |

## C# 14 feature matrix

| C# 14 feature | Compiles (net10.0) | JIT status | Notes |
|---------------|--------------------|------------|-------|
| Field-backed properties (`field` keyword) | YES | Tested by `p4cs14`; results not yet green | The backing field is an ordinary compiler-generated field; no special JIT work identified. `p4cs14` must pass for sign-off. |
| Extension members (`extension(...)` blocks) | YES | Same as above | Lowered to static methods with `[Extension]`; ordinary call patterns. |
| Null-conditional assignment (`a?.b = c`) | YES | Same as above | Conditional-branch pattern; no new opcodes. |
| Simple lambda parameters with modifiers (`(ref x) => ...`) | YES | Same as above | Custom delegate + byref parameter; exercises delegate ctor with byref signature. |
| Partial properties | YES | Same as above | Ordinary property lowering. |
| Unbound generics in `nameof` (`nameof(List<>)`) | YES | Compile-time only | No runtime metadata pattern. |
| Implicit span conversions (`ReadOnlySpan<char> s = "abc"`) | NO (app model) | BLOCKED | The app-facing IL BCL (`korlib.dll`) does not ship `Span<T>`/`ReadOnlySpan<T>` yet (the kernel AOT path has them via bflat-excluded sources). Tracked in PHASE4-REPORT.md. |
| `yield return` iterators (app + korlib) | NO | BLOCKED (compiler model) | With `NoStdLib`/korlib-as-corlib the compiler's iterator-interface check rejects iterator blocks (CS1624: "`IEnumerable<T>` is not an iterator interface type") because korlib's own `System.Collections.Generic.IEnumerable<T>` shadows the one span. Hand-written enumerators are the supported pattern; korlib's LINQ uses them. |
| `async`/`await` (state machines) | YES | PARTIAL | `Task`/`AsyncTaskMethodBuilder`/`TaskAwaiter` exist and simple awaits compile; the `p4async` test app completes `Task.Run` + `.Result` + `.Wait` but currently hangs later in the file (see PHASE4-REPORT.md). |
| `System.Linq.AsyncEnumerable` (referenced assemblies) | n/a | PENDING | Not yet present in korlib; assemblies referencing it will not resolve the type yet. |

## JIT changes made in Phase 4

1. **`newobj` shadow-reserve alignment** (`ILCompiler.CompileNewobj`):
   the reserve is padded by `(16 - ((_evalStackByteSize + reserve) & 15)) & 15`
   so the `RhpNewFast` call and the constructor call keep the ABI parity
   with pending eval-stack bytes. (The JIT keeps temporary values on the
   machine stack, so frame-size arithmetic alone is not sufficient.)
   Without this, the 8-off delta propagated through JIT frames until an
   AOT callee `movaps` faulted (boot-test `#GP` in `GetSchedulerStats`).
2. **Allocation-shim alignment** (`native.asm` `jit_new_fast_shim` /
   `jit_new_array_shim`): `RhpNewFast`/`RhpNewArray` are called through
   the generic alignment shims like the other runtime helpers.
3. **Stack-arguments call-frame alignment** (`ILCompiler.CompileCall`):
   JIT->AOT calls with 4+ arguments are emitted raw (the shim cannot be
   used because its frame would move the stack arguments); the call frame
   is now padded with `stackArgAlignmentPad` so those call sites keep the
   same parity rule. Frame sizes are computed once at allocation time and
   reused at cleanup time.
4. **Crash diagnostics** (`Arch`): the raw exception path dumps the first
   24 stack words after `RAWV` for offline call-chain reconstruction.

## Known open JIT issues (blocking the Phase 4 acceptance items)

| Issue | Symptom | Where |
|-------|---------|-------|
| Virtual/interface call sites with stack args are not parity-corrected | `movaps` #GP inside `MethodTable.GetInterfaceMethodSlot` (via `RhpResolveInterfaceMethod` in the interface dispatch stub) for the file I/O write path | `ILCompiler` callvirt emission (the second, larger emission path around the `callvirt` handler); the stub chain `RhpInitialDynamicInterfaceDispatch -> RhpResolveInterfaceMethod -> GetInterfaceMethodSlot` assumes standard call-site alignment, so the misalignment must be fixed in the JIT caller. |
| Unresolved `newobj` token in generic contexts | `[JIT newobj] FAIL: unresolved token 0x0A000032` aborts `p4linq` at the first `new List<int>()` | `ILCompiler.CompileNewobj` MemberRef resolution for BCL generic ctors. |
| `async` state machine hang | `p4async` completes the first two checks, then stops responding | Suspected await continuation path (`AsyncTaskMethodBuilder`/`TaskAwaiter` interaction with the synchronous Task); under investigation. |
| `p4cs14` produced no serial output | needs a dedicated re-run with serial capture | re-run and fix. |

## Guidance for application authors (current state)

- Prefer plain loops over `yield return` in application code.
- Prefer `Task.Run(...).Result`/`.Wait()` style over deep await chains.
- Avoid `Span<T>`/`ReadOnlySpan<T>` in application code for now.
- Keep assembly names FAT-short (<= 8 chars).
- `System.IO` file writes currently fault on the write path (see the
  open issues table); reads work.

These constraints shrink as the JIT issues above are fixed; the test
suite (`tests/run-phase4-tests.ps1`) is the guard for that work.
