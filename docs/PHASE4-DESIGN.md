# PHASE4-DESIGN.md - cross-assembly loading, BCL bridges, async limitation

## 1. Assembly model

NeutrinoOS runs three kinds of managed code:

1. **Kernel AOT** - bflat `Zero`-mode compilation of `src/kernel/**` plus
   the shared korlib sources (no BCL; korlib *is* the BCL in this build).
2. **Preloaded JIT assemblies** - loaded by the bootloader from the boot
   image before `ExitBootServices` (`korlib.dll`, `ProtonOS.DDK.dll`,
   the driver assemblies, test assemblies). The kernel registers them
   with the `AssemblyLoader` at boot.
3. **On-demand assemblies** - application DLLs (`/apps/*.dll` via the
   `run` command) and libraries resolved from `/lib/*.dll`.

### 1.1 Virtual assembly redirect

Applications build against the normal .NET 10 reference assemblies; at
load time their BCL AssemblyRefs resolve to **korlib** through
`AssemblyLoader.IsVirtualAssembly`: `System.Runtime`,
`System.Private.CoreLib`, `System.Threading*`, `System.Collections`,
`System.Console`, `System.Runtime.Extensions`, `System.Text.Encoding`,
`System.Linq` (added in Phase 4), and `netstandard`. Type and member
resolution then walks korlib's metadata; members that korlib does not
implement surface as JIT "no code" diagnostics.

### 1.2 On-demand `/lib` loading (Phase 4, Task 4)

`AssemblyLoader.ResolveAssemblyRef` gained a final fallback: when an
AssemblyRef cannot be found among loaded assemblies (and is not a virtual
BCL name), `TryLoadAssemblyFromLib`:

1. builds `/lib/<name>.dll` as UTF-16,
2. reads it through `Platform.FileExports.KernelBootSize/KernelBootRead`
   (the same kernel file bridge System.IO uses),
3. loads the PE through the regular `AssemblyLoader.Load` path.

The bytes stay in kernel heap memory for the lifetime of the boot (the
loader keeps referencing the image; the same contract `run` images use).
Resolution failures (missing file, no driver) degrade to the previous
"NOT FOUND" behavior.

`FileExports.EnsureDriverHelpers` JIT-compiles the driver helpers once,
lazily, after the AHCI driver is bound. A reentrancy guard
(`_ensureInProgress`) breaks the potential recursion
(helper compilation -> assembly resolution -> file bridge -> helper
compilation).

### 1.3 Entry point invocation

`Platform.AssemblyRunner.Run(path, args)` (driven by the shell `run`
command) loads the PE, finds the CLI entry-point token, inspects the
`Main` signature (`NetExecutable.GetMainSignatureInfo`) and invokes it in
kernel mode. Argument arrays and strings are materialized **inside a
JIT-compiled frame** (`TestSupport.ShellRunSupport.InvokeMain`) so the GC
sees them as roots; `ShellRunSupport` pins the materialized `string[]` in
a static field across the call (the established JIT-side rooting
pattern).

## 2. BCL bridges (korlib <-> kernel)

Two bridge styles are in use, both keyed off method tokens in the korlib
metadata:

- **`#if KORLIB_IL` stubs + token registry.** korlib declares private
  static primitives that throw `PlatformNotSupportedException` in the IL
  build; `Kernel.BuildKorlibTokenRegistry` / `BuildConsoleTokenRegistry` /
  `BuildDDKTokenRegistry` / **`BuildFileTokenRegistry` (Phase 4)** map the
  matching korlib MethodDef tokens to kernel `[UnmanagedCallersOnly]`
  export addresses. JIT-compiled calls to those primitives jump straight
  into kernel AOT code.
- **`#else` `[DllImport("*")]`** - the same primitives bind to the native
  exports when the kernel AOT build compiles korlib's real-HAL path.

### 2.1 The file bridge

```
System.IO.File/Directory (korlib, JIT-compiled for apps)
  -> FileBootRead/FileBootWrite/FileBootSize/FileBootExists/FileBootDelete
     DirBootExists/DirBootCreate/DirBootDelete/DirBootEntry   (kernel exports)
  -> Platform.FileExports (AOT) --function pointers-->
     AhciEntry helpers (JIT-compiled once, ahci driver assembly)
  -> FatFileSystem / FatFileHandle / FatDirectoryHandle (fat driver assembly)
  -> AHCI driver -> boot disk
```

Raw pointers are passed across the boundary (pinned char*/byte* buffers
via `fixed`); no managed references cross. Long-lived bootstrap objects
inside the driver helpers (`FatFileSystem`, handles) are pinned in static
fields (`_pinnedFat`, `_pinnedFile`, `_pinnedDir`) - JIT-assembly statics
are GC roots, and JIT frames can miss locals across mid-call collections
(this was the established fix for the path-string, applied to the Phase 4
helpers).

### 2.2 Alignment contract (critical for drivers/helpers)

JIT-compiled code may call AOT/kernel code at **any** RSP parity; AOT
callees assume the standard parity (callee entry RSP % 16 == 8, which
their SSE frame stores rely on). Therefore, JIT call sites to AOT targets
must be parity-corrected:

- 0-register-arg AOT target calls route through `jit_align_call`
  (`JitStubs.AlignCallAddress`) - the shim re-aligns around the call.
- `newobj` and raw stack-arg calls pad their stack frames by the modulus
  `(16 - ((_evalStackByteSize + frame) & 15)) & 15` (Phase 4 fixes).
- Open: virtual/interface call sites with stack args still need the same
  treatment (see PHASE4-JIT-COMPAT.md "Known open JIT issues").

## 3. `System.Threading.Tasks` limitation

korlib's `Task` is a **synchronous, single-threaded** implementation:
`Task.Run` executes the delegate inline on the calling thread, `Task.Delay`
sleeps (timer-backed), continuations run inline, and `Wait()`/`Result`
return once the operation completed. `async`/`await` state machines are
therefore effectively synchronous: there is no task scheduler, no
work-stealing, and awaiting does not change threads.

Documented consequences:

- No true concurrency in Phase 4; CPU-bound `Task.Run` blocks the caller.
- `Task.WhenAll` folds to sequential completion.
- `ConfigureAwait` is accepted and ignored.
- The `p4async` test app exercises the state-machine lowering and passes
  all checks (including chained awaits); a truly deferred completion
  path remains unverified - see PHASE4-JIT-COMPAT.md "Known open JIT
  issues".

A real scheduler (work queues per CPU, awaitable timers) is a later-phase
deliverable; Phase 5+ networking/timers will build on it.

## 4. Whole-file `FileStream` design

The kernel file bridge transfers whole files (size probe + read; whole
write with create/truncate/append). `FileStream` therefore reads the file
into a managed buffer on open and performs a single truncated rewrite on
Flush/Close. This preserves the observable semantics for the single-writer
console-scale files NeutrinoOS targets. Random-access streaming and
concurrent writers require an open-handle bridge (planned later); the
bridge surface was deliberately kept small for Phase 4.

## 5. GC rooting rules for JIT-side code (established practice)

1. Objects created inside JIT-compiled frames and kept across
   allocations that may trigger a GC **must** be reachable from a JIT
   static field (JIT-assembly statics are GC roots).
2. Kernel AOT code must never hold managed references across a JIT call
   boundary; data crosses as raw pointers into pinned buffers.
3. Path strings and bootstrap objects used by driver helpers are pinned in
   statics for the duration of each call (`_pinnedBootPath`, `_pinnedFat`,
   `_pinnedFile`, `_pinnedDir`).
