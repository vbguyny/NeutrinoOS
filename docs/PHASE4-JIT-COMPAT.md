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
   24 stack words after `RAWV` for offline call-chain reconstruction, plus
   `CR2` on page faults.

### Alignment-stack completion (post-audit)

The remaining virtual/interface call misalignment blocker was resolved by
finding that four independent bugs stacked together:

5. **`RhpStackProbe` ABI** (`native.asm`): the bflat/ILC code generator
   emits the R11-target calling convention
   (`lea r11,[rsp-N]; call RhpStackProbe; mov rsp,r11`). The previous
   implementation read the probe length from `rax` (Windows chkstk style);
   with whatever the caller left in `rax` it walked multi-megabyte spans
   and page-faulted on unmapped memory. Rewritten to the R11 convention.
6. **Interface dispatch stub** (`RhpInitialDynamicInterfaceDispatch`):
   RSP is normalized (16-byte aligned) around the resolver call with a
   **callee-saved (RBX) anchor** - R11 is volatile across the resolver call
   and restoring `rsp` from it put wild values into RSP. The shadow space
   is reserved 48 bytes below the saved argument registers so the callee's
   register homing cannot clobber them.
7. **Exact eval-stack accounting** (`ILCompiler`): branch sources record
   the eval-stack byte size together with the depth (packed `ushort`), and
   merge targets restore both verbatim (re-deriving the size from the entry
   list picked up the other arm's shape at merges).
   `CompileWithFunclets` pass-1 (the emitted main body of EH methods) now
   performs the same branch-merge resync as `Compile()`, and `CompileLeave`
   resets `_evalStackByteSize` (previously depth-only).
8. **Parity pads re-enabled** now that the accounting is exact:
   `callvirt` and the raw 0-4 argument call frames pad by
   `(16 - (_evalStackByteSize & 15)) & 15` before the call and fold the
   same amount into the cleanup.
9. **`RhpStackProbe` / `[PHYS]` diagnostics** (`X64Emitter` +
   `ILCompiler.CheckPhysStack`): the emitter accumulates its own RSP
   deltas and the compiler compares them with the tracked eval-stack byte
   size at every opcode boundary; divergences are printed with the
   previous opcode that caused them.
10. **Private execution stack** (`BigStackRunner` + `run_on_big_stack`):
    the whole `run` pipeline (file load, triggered JIT compiles, `Main`)
    executes on a private 8 MB stack allocated from the page allocator;
    the boot firmware stack (tens of KB) cannot host the nested compile
    chains of a standard app.
11. **Abstract-method AOT bridges** (`MetadataIntegration`
    `TryResolveAbstractMethodAotBridge` + `AotMethodRegistry`
    `RegisterEncodingMethods`): abstract methods on korlib-backed types
    (`System.Text.Encoding.GetBytes`/`GetString`/`GetByteCount`/
    `GetCharCount`/`get_EncodingName`) are resolved to their kernel bridge
    forwarders instead of vtable dispatch - NativeAOT-optimized AOT
    vtables carry no entries for those slots (UTF8Encoding `vtable[5]` is
    empty), which previously halted in `EnsureVtableSlotCompiled`.

### LINQ unlock (generic MemberRef + vtable slot registration)

12. **Array type arguments in signatures**
    (`AssemblyLoader.ParseTypeFromSignature`): `ELEMENT_TYPE_SZARRAY`
    (0x1D) and `ELEMENT_TYPE_ARRAY` (0x14) now parse to the array
    MethodTable of the element type. Previously arrays as type arguments
    hit the primitive lookup and returned null, aborting
    generic-instantiation resolution (`[JIT newobj] FAIL: unresolved
    token 0x0A000032` on the first `new List<int>()` in `p4linq`).
13. **GenParamCount skip for generic signatures** (three parsers in
    `MetadataIntegration`): method signatures with the GENERIC flag
    (0x10) carry a compressed GenParamCount *before* ParamCount. Reading
    ParamCount first mis-sized e.g. `IOrderedEnumerable<T>.
    CreateOrderedEnumerable<TKey>` as 1 arg, and the call emitter set up
    the wrong argument registers.
14. **MemberRef-backed MethodSpec recompilation guard**
    (`MetadataIntegration.CompleteMethodResolution`): when a resolved
    generic method entry was compiled for a different instantiation
    (TypeArgHash mismatch), it is invalidated and recompiled under the
    current method type args instead of reusing stale code (e.g. `[int]`
    code executed for `[string]`).
15. **Exact explicit-slot protection during vtable registration**
    (`AssemblyLoader.RegisterNewVirtualMethodsForLazyJit`): the previous
    per-interface bitmask ("interfaces with any explicit implementation")
    excluded whole interfaces, so an implicit `IEnumerator.MoveNext`
    registered at a bogus sequential slot instead of interface slot 5
    (explicit `IEnumerator.get_Current` was the only protected method).
    Result before the fix: an int-flavored `MoveNext` compiled for
    `List<int>.Enumerator` was resolved for `List<string>.Enumerator`
    (4-byte element stride) and corrupted the `OrderBy` sort buffer. The
    mask is replaced by an exact list of claimed slots.
16. **Instantiation-aware vtable slot propagation**
    (`AssemblyLoader.PropagateVtableSlotToInstantiations`): when
    propagating compiled code into cached instantiations' empty slots,
    entries whose type arguments differ from the compiling context are
    skipped, so instantiation-specific code never leaks across
    instantiations.
17. **Lowest-registered-slot preference** (`Tier0JIT.PopulateVtableSlot`):
    token lookups can return an entry without a slot (a compiled flavor);
    the lowest slot-registered entry for the token is preferred so
    interface implementations always land in their registered slot. The
    vtable-slot hint is also cleared unconditionally after a compile
    attempt (a cache-hit compile never consumed it, leaving a stale hint
    that could corrupt a later unrelated compile).
18. **Interface-dispatch fallbacks** (`MethodTable.TypeHelpers` +
    `JitStubs.ResolveInterfaceMethodByName`): arrays (which carry no
    interface map) map `IEnumerable<T>.GetEnumerator` to a new korlib
    `SZGenericArrayEnumerator<T>` factory and `ICollection<T>.get_Count`
    to the array length; other types with incomplete maps resolve the
    implementation by name (exact or explicit `Prefix.Name`) down the
    class hierarchy and compile it with the instantiation's type-argument
    context.
19. **`constrained.` with reference types and arguments**
    (`ILCompiler` constrained-callvirt branch): the eval stack holds
    `[managed_ptr, arg0..argN]` with the args *above* the pointer; the
    code now saves the argument registers before dereferencing the
    pointer (previously it dereferenced the last argument; with no
    arguments the bug was invisible, which is why simple constrained
    calls worked).
20. **`unbox`/`unbox.any` on reference types are no-ops** (`ILCompiler`):
    the old code applied value-type width handling, so unboxing a string
    produced a 16-bit load from the object's field area (`string`'s
    ComponentSize is 2). Reference-type targets now leave the object
    reference on the eval stack unchanged.
21. **korlib LINQ semantics fixes** (`System.Linq.Enumerable`,
    `System.Collections.Generic.Comparer`): `CompareKeyChain` compared the
    level list outermost-first, making `ThenBy` the primary key; it now
    walks from the root `OrderBy` level outwards. `ObjectComparer<T>`
    gained ordinal string comparison (matching `Comparer<string>.Default`)
    and now uses the static `object.Equals` for equality - instance
    `x.Equals(y)` returns false for equal boxed ints in this runtime,
    which made equal sort keys compare unequal. `p4linq` passes 34/34
    checks after these fixes.

## Known open JIT issues (blocking the Phase 4 acceptance items)

| Issue | Symptom | Where |
|-------|---------|-------|
| JIT-type vtable registration gaps beyond the resolved Encoding case | `[JitStubs] FATAL: VTable slot N has no registered method for MT 0x33Fxxx` (e.g. slot 20/MT `0x33F248`, asm=1 type `0x020000C9`) during `p4fileio`'s later checks (instance disposal / writer flushes). Ancestor/interface resolution succeeds for neighbouring slots (4, 17, 21) but not this one. | `CompiledMethodRegistry` registration coverage for inherited virtuals on JIT-created types; extend the ancestor/interface fallback in `JitStubs.EnsureVtableSlotCompiled` or the per-type slot registration. |
| `async` state machine hang | `p4async` completes the first two checks, then stops responding | Suspected await continuation path (`AsyncTaskMethodBuilder`/`TaskAwaiter` interaction with the synchronous Task); under investigation. |
| `p4fileio` "two lines" check | `[fileio] ok: line 1` then `[fileio] FAIL: line 2` - the second line written does not read back | Data-level issue in the write/read round trip (see `tests/phase4/FileIo/Program.cs`); separate from the vtable/alignment work. |
| `p4cs14` produced no serial output | needs a dedicated re-run with serial capture | re-run and fix. |

## Fixed since the initial audit (kept for history)

| Former issue | Status |
|--------------|--------|
| Virtual/interface call sites not parity-corrected (`movaps` `#GP` inside `MethodTable.GetInterfaceMethodSlot` via the dispatch stub, file I/O write path) | FIXED - root causes were the `RhpStackProbe` ABI mismatch (item 5), the dispatch-stub RSP restore anchor (item 6), merge-point byte-size accounting (item 7) and the resulting wrong/absent parity pads (items 3, 8). `run /apps/p4fileio.dll` now executes the write path and completes several further checks. |

## Guidance for application authors (current state)

- Prefer plain loops over `yield return` in application code.
- Prefer `Task.Run(...).Result`/`.Wait()` style over deep await chains.
- Avoid `Span<T>`/`ReadOnlySpan<T>` in application code for now.
- Keep assembly names FAT-short (<= 8 chars).
- `System.IO` file path: the write path is functional; the "two lines"
  round-trip check still fails on its second line (see the open issues
  table).

These constraints shrink as the JIT issues above are fixed; the test
suite (`tests/run-phase4-tests.ps1`) is the guard for that work.
