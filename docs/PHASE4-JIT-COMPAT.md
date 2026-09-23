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
| Field-backed properties (`field` keyword) | YES | PASS | Tested by `p4cs14` (exit 0). The backing field is an ordinary compiler-generated field; no special JIT work identified. The earlier failures were a MemberRef-resolution gap for inherited methods (`ex.GetType()` - see item 26), not the C# 14 feature lowering. |
| Extension members (`extension(...)` blocks) | YES | PASS | Lowered to static methods with `[Extension]`; ordinary call patterns. Verified with `p4cs14`. |
| Null-conditional assignment (`a?.b = c`) | YES | PASS | Conditional-branch pattern; no new opcodes. Verified with `p4cs14`. |
| Simple lambda parameters with modifiers (`(ref x) => ...`) | YES | PASS | Custom delegate + byref parameter; exercises delegate ctor with byref signature. Verified with `p4cs14`. |
| Partial properties | YES | PASS | Ordinary property lowering. Verified with `p4cs14`. |
| Unbound generics in `nameof` (`nameof(List<>)`) | YES | Compile-time only | No runtime metadata pattern. |
| Implicit span conversions (`ReadOnlySpan<char> s = "abc"`) | NO (app model) | BLOCKED | The app-facing IL BCL (`korlib.dll`) does not ship `Span<T>`/`ReadOnlySpan<T>` yet (the kernel AOT path has them via bflat-excluded sources). Tracked in PHASE4-REPORT.md. |
| `yield return` iterators (app + korlib) | NO | BLOCKED (compiler model) | With `NoStdLib`/korlib-as-corlib the compiler's iterator-interface check rejects iterator blocks (CS1624: "`IEnumerable<T>` is not an iterator interface type") because korlib's own `System.Collections.Generic.IEnumerable<T>` shadows the one span. Hand-written enumerators are the supported pattern; korlib's LINQ uses them. |
| `async`/`await` (state machines) | YES | PASS | `p4async` passes all 6 checks (exit 0): `Task.Run` + `.Result` + `.Wait`, `await Task.Delay`, chained awaits (`await` of a `Task<int>` returned by another async method), `Task.FromResult`, async completion. Fixes: `constrained.` calls on the state-machine struct no longer box (item 27), type-argument contexts survive nested compiles (item 28), and generic-method instantiations are verified per instantiation instead of shared (item 29). |
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
22. **Override slot registration aligns to the base method's registered
    slot** (`AssemblyLoader.FindVirtualMethodSlotByName`): derived
    overrides previously took the sequentially counted base slot, which
    disagrees with the registered slot whenever interface slots are
    interleaved with a type's new-slot virtuals. `TextWriter`'s
    `IDisposable` occupies slot 3, so its `Close` is registered at slot
    20, while `StreamWriter.Close` was registered at the counted 19 and
    `StreamWriter.Dispose` at 20. `StreamWriter.Dispose`'s
    `callvirt TextWriter.Close` therefore re-entered `Dispose` (infinite
    self-recursion, `RAWV` at the dispatch return site / `FATAL: VTable
    slot 20`). The lookup now prefers the base method's registry entry
    slot; it is used both for the LazyJIT override registrations and by
    `Tier0JIT.ComputeVtableSlot` (item 23).
23. **Abstract-method entries use the override-registration numbering**
    (`Tier0JIT.ComputeVtableSlot`): the fall-through count was 0-based
    within the declaring type, so an abstract-method entry could land on
    an unrelated slot (`Stream.Flush` counted 6 = `FileStream.get_Length`
    in the registration numbering). `FileStream.Close`'s
    `callvirt Stream.Flush` then compiled and invoked `get_Length`
    instead of `Flush`, so the file was never flushed and
    `File.OpenRead` later threw `FileNotFoundException`. The fall-through
    now uses `AssemblyLoader.FindVirtualMethodSlotByName` /
    `FindVtableSlotInBaseClass` (base slots + new-slot index), matching
    the derived override registrations.
24. **Interface dispatch stub aligns the resolver call**
    (`RhpInitialDynamicInterfaceDispatch` in `native.asm`): the stub
    tail-calls the resolved method with the caller's entry parity (by
    design) and called `RhpResolveInterfaceMethod` with a fixed 40-byte
    frame, which is only correct when the entry parity is the ABI's.
    A caller with the opposite parity misaligned the resolver chain and
    `#GP`'d inside `MethodTable.GetInterfaceMethodSlot`'s aligned SSE
    frame stores (`movaps [rsp+x]`). The resolver call now forces 16-byte
    alignment (`push rbp; mov rbp,rsp; and rsp,-16; sub rsp,32; call;
    mov rsp,rbp; pop rbp`), which is safe for either entry parity.
25. **FAT directory entry index is the global 32-byte slot**
    (`src/drivers/shared/storage/fat/FatFileSystem.cs`): `FindEntry`'s
    cluster-chain branch counted only live entries (deleted, volume-label
    and LFN slots were skipped without incrementing the index), while
    `UpdateDirectoryEntryPartial`, `DeleteDirectoryEntry` and
    `CreateEntryInDirectory` treat the index as the global slot in the
    directory's cluster chain. Appending to a file whose directory slot
    sits after an LFN entry therefore flushed the new file size into the
    wrong slot and the appended data never became visible (the
    `p4fileio` "two lines" failure). The lookup now counts every slot.
26. **Inherited-method resolution walks the korlib base chain**
    (`AssemblyLoader.TryResolveAotInherited`, used both by the
    well-known-type MemberRef fallback and by `MetadataIntegration`'s
    synthetic-AOT re-lookup): a MemberRef may name a method the
    declaring type does not itself define (the `p4cs14` app's `Fail()`
    helper calls `ex.GetType()`; `GetType` is declared on
    `System.Object`, not `Exception`). The walk resolves the first chain
    level that has an AOT registry entry, or that declares the method in
    IL (own-body wins over inherited), and handles reference-BCL bases
    such as `System.Object` that exist only as external TypeRefs.
    `p4cs14`'s `Fail()` helper compiles and runs once
    `Exception.GetType` resolves to the `ObjectHelpers.GetType` AOT
    bridge.
27. **`constrained.` callvirt on a value type calls the interface
    implementation directly** (`ILCompiler.CompileCallvirt`, constrained
    value-type branch): the previous fallback boxed the value and ran the
    method on the box - mutations were lost and `Start<>(ref stateMachine)`
    completed a different builder task than the caller read, so
    `.Result` spun forever in `Task.SpinWait`. The branch now resolves
    the implementation on the constraint type
    (`JitStubs.ResolveInterfaceMethodByName` first - type-checked against
    the constraint type's own metadata - with the interface-map slot as
    fallback) and emits a direct call with the managed pointer as `this`.
28. **Type-argument context is saved/restored per compilation**
    (`MetadataIntegration.Push/PopMethodTypeArgContext`, hooked into
    `Tier0JIT.CompileMethod`/`RestoreContext`): nested compiles parse
    MethodSpecs and overwrite the context arrays, previously leaking
    residue into the enclosing compile (MVAR TypeSpecs then resolved
    against stale or null entries and `box`/`constrained.` emission
    failed). Also, `ParseMethodSpecInstantiation`'s `GenericInst` case
    now resolves the instantiated generic MethodTable via
    `AssemblyLoader.ParseTypeFromSignature` instead of only recording a
    size (method type args like `TaskAwaiter<int>` arrived with a null
    MethodTable before).
29. **Generic-method instantiation caching is verified per
    instantiation** (`MetadataIntegration.ResolveMethodSpecMethod`): the
    instantiation hash (`TypeArgHash`) was only stamped for
    MethodDef-backed specs (`tag == 0`), so MemberRef-backed specs - all
    `AsyncTaskMethodBuilder.Start<TStateMachine>` call sites - could
    reuse a native compiled for a different state-machine type
    (`Start<d__2>` served `Start<d__3>`, running the wrong `MoveNext`).
    The stamp/re-verify now covers both spec forms; each state-machine
    type gets its own `Start` code.

## Known open JIT issues (blocking the Phase 4 acceptance items)

| Issue | Symptom | Where |
|-------|---------|-------|
| Asynchronous suspension (awaits that complete later) | Not exercised by the current tests: the synchronous Task model completes every awaited task before `IsCompleted` is probed, so continuation registration/boxing never takes a truly deferred path | `AsyncTaskMethodBuilder`/`TaskAwaiter` in korlib; the `StateMachineContinuation` path is unverified |

## Fixed since the initial audit (kept for history)

| Former issue | Status |
|--------------|--------|
| `async` state machine hang (`p4async` completed checks 1-2, then spun forever) | FIXED - `Start<TStateMachine>`'s `constrained.` call boxed the state machine (mutations landed on the box copy; the caller's `builder.Task` stayed incomplete and `.Result` spun in `Task.SpinWait`), and `Start<T>` instantiations for different state machines shared one compiled native. Items 27-29. `p4async` passes 6/6 checks, exit 0. |
| `p4cs14` produced no serial output | FIXED - `Exception.GetType` (declared on `System.Object`) failed MemberRef resolution (item 26), so `Main`'s codegen failed before any test output. `p4cs14` passes, exit 0. |
| Virtual/interface call sites not parity-corrected (`movaps` `#GP` inside `MethodTable.GetInterfaceMethodSlot` via the dispatch stub, file I/O write path) | FIXED - root causes were the `RhpStackProbe` ABI mismatch (item 5), the dispatch-stub RSP restore anchor (item 6), merge-point byte-size accounting (item 7), the resulting wrong/absent parity pads (items 3, 8), and the dispatch stub's fixed-frame resolver call, now alignment-normalized (item 24). |
| `[JitStubs] FATAL: VTable slot N has no registered method` during `p4fileio`'s disposal path | FIXED - derived override registrations and abstract entries used different slot numbering than the base-class registrations (items 22, 23). |
| `p4fileio` "two lines" check (`FAIL: line 2`) | FIXED - FAT driver entry-index semantics (item 25). `p4fileio` now passes 16/16 checks with exit code 0. |

## Guidance for application authors (current state)

- Prefer plain loops over `yield return` in application code.
- `async`/`await` works for synchronous-completing tasks (the Phase 4
  model); deep await chains are fine. True suspension (tasks completing
  after the caller blocks) is unverified - see Known open JIT issues.
- Avoid `Span<T>`/`ReadOnlySpan<T>` in application code for now.
- Keep assembly names FAT-short (<= 8 chars).
- `System.IO`: file create/append/read/delete, `FileStream`,
  `StreamWriter`/`StreamReader` and `Directory` enumeration all pass the
  `p4fileio` acceptance suite.

These constraints shrink as the JIT issues above are fixed; the test
suite (`tests/run-phase4-tests.ps1`) is the guard for that work.
