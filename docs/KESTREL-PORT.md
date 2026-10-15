# KESTREL-PORT.md — porting Kestrel so WebAPI projects run on NeutrinoOS

**Status: in progress.** Milestone 1 is implemented, verified under QEMU and
reachable from a Windows 11 host (see `docs/samples/webapi/`). This document
is the plan for the remaining milestones and supersedes the "Kestrel is a
Phase 7+ goal" note in `docs/PHASE6-WEB.md`.

## Goal

Run ASP.NET Core-style Web API projects (the `Microsoft.NET.Sdk.Web`
programming model: `WebApplication.CreateBuilder`, `MapGet`/`MapPost`,
`Results`, eventual `KestrelServer`) on NeutrinoOS, hosted cooperatively by
the kernel's service model, and reachable from the network like the existing
`webhost` fallback server.

## Why staged

Kestrel proper depends on machinery the Tier-0 JIT world does not fully
provide yet: `System.IO.Pipelines`, `Memory<T>`/`ReadOnlySequence<byte>`,
`ArrayPool<T>`, deep async/`ValueTask` flows, thread-pool scheduling, DI and
logging abstractions. Porting "all of Kestrel" in one step is not feasible,
so the port proceeds in milestones, each one **runnable and testable on the
device** before the next begins. The user-facing API (the part app authors
write) is fixed from M1 onward; only the engine underneath is replaced.

## What already exists (inventory, verified)

| Layer | State |
|---|---|
| Async runtime | `Task`, `TaskAwaiter`, `ValueTask`, `ValueTaskAwaiter`, `AsyncMethodBuilder`, `AsyncStateMachineAttribute`, `IAsyncStateMachine`, `ConfiguredTaskAwaitable`, `TaskCompletionSource`, `TaskStatus`, `CancellationToken(Source)` all exist in korlib (`src/korlib/System/Threading/Tasks/`). |
| Memory primitives | `Span<T>`, `ReadOnlySpan<T>`, `Unsafe`, `MemoryMarshal`, `Memory<T>`, `ReadOnlyMemory<T>`, `ArrayPool<T>`, `IBufferWriter<T>`, `ReadOnlySequence<T>`/`SequencePosition`, `System.IO.Pipelines` (`Pipe`, `PipeReader`, `PipeWriter`, `PipeOptions`) all exist in korlib (`src/korlib/System/`, `src/korlib/System/Buffers/`, `src/korlib/System/IO/Pipelines/`) and are covered by the on-device `Memory/Pipelines` JITTest category. |
| Threading | `Thread`, `Monitor`, `Interlocked` via the kernel scheduler (no thread pool). |
| Sockets | DDK `TcpServer`/`TcpSocket` with cooperative `NetworkPump` (proven by `sshd`, `webhost`, `sampleapi`, `socktest`). Blocking helpers (`Accept(timeoutMs)`, `ReceiveWait`). |
| HTTP/1.1 server logic | Proven parsers/handlers in `WebService` (plus HTTP/2, HTTP/3, TLS 1.3 for the fallback surface) and in `SampleApi` (request head parse, `Content-Length` body wait, 431/413 limits). |
| Strings/JSON | korlib `String`, `StringBuilder`, `UTF8Encoding`; hand-rolled JSON in the samples (no `System.Text.Json` yet). |
| Hosting | Cooperative service contract: `Start()`/`Tick()`/`Stop()` JIT-compiled from the DDK by `ServiceRegistry`, `Tick()` driven from the shell idle hook. |

## Milestones

### M1 — WebApplication surface + host engine (this commit) ✅

- `src/ddk/Web/HttpContext.cs` — `HttpContext`, `HttpRequest` (Method, Path,
  QueryString, Body, `Query(key)`, `Route(name)`), `HttpResponse`
  (`StatusCode`, `ContentType`, `Write`, `Json`, `Text`, `Status`),
  `RequestHandler` delegate, `WebStatus` reason phrases.
- `src/ddk/Web/WebHost.cs` — the engine: `TcpServer` listener, 4 connection
  slots, head parse + body wait, route table (`MapGet`-style patterns with
  `{id}` captures), 404/405 semantics, one request per connection
  (`Connection: close`), driven by `Tick()`.
- `src/ddk/Web/WebApplication.cs` — `WebApplication.CreateBuilder(args)`
  (builder + `Build()` fused), `MapGet/MapPost/MapPut/MapDelete` returning
  the app for chaining, `Start()/Tick()/Stop()` service contract.
- Sample app: `src/ddk/Samples/TasksWebApp.cs` — the task-tracker REST API
  written declaratively in the new style (handlers ≈ 1/4 the code of the
  hand-wired `SampleApi`), plus the `webapi` utility
  (`src/utilities/webapi`) and kernel registration.
- Verification: `build/webapi-probe.sh` — **29/29 PASS** (discovery, echo,
  CRUD, 400/404/405 semantics, concurrency, guest start/stop/restart),
  `sampleapi` regression 28/28, HTTP checks from the host through
  `hostfwd 18080 -> 8080`.
- Honest limits (documented in the sample README): synchronous handlers
  only, no `app.Run()` blocking loop (the kernel drives `Tick()`), no
  DI/logging surface yet. (Keep-alive arrived with the M2 `WebHost`
  engine for the `webapi` sample; the M1 `SampleApi`/`sampleapi` path
  still closes every response.)

### M2 — Pipelines + async transport

**Step 1 DONE (this commit).** korlib now ships the memory/pipeline
primitives and they are exercised on-device by the `Memory/Pipelines`
JITTest category (30+ assertions over `Memory<byte>`, `Span` interplay,
`ArrayPool` rent/return/reuse, `ReadOnlySequence` linked segments, `Pipe`
commit/consume semantics, backpressure, a 1 MB pump and async-state-machine
awaiter paths). Getting them trustworthy required fixing four JIT/runtime
bugs, all in this commit:

- **Large-struct argument pointers are pad-aware** (`ILCompiler.CompileCall`
deferred RCX/RDX LEAs): the deferred struct-pointer setup omitted the
`loadArgAlignmentPad` bytes, so every large-struct argument (e.g.
`Memory<T>`, 16 bytes) landed 8 bytes off whenever the eval-stack byte size
was not 16-byte aligned. This was the bulk of the "Equals always false"
saga.
- **Boxed value-type virtual dispatch adjusts `this` to the box payload**
(`ILCompiler.CompileCallvirt` virtual-dispatch emission): the
interface-dispatch path already added +8 for boxed value types (ECMA-335
boxed-instance convention); the virtual-dispatch path did not, so a
JIT-compiled struct override invoked through a box read the box's
MethodTable word as its first field. Mirror of the interface-path logic is
now in both virtual sub-paths.
- **Constrained `callvirt` on value types copies the full struct into the
receiver box** (`ILCompiler` boxing fallback): the fallback boxed only 4/8
bytes of the receiver, leaving the rest of a large struct zeroed (that is
how `Memory<T>`'s `_length` stayed 0 and made the boxed receiver compare
unequal).
- **`Nullable<T>` layout heuristic is gated to real `Nullable<T>`**
(`Tier0JIT.GetValueTypeSigWithSize`): ANY single-type-argument generic
struct with a small primitive argument (`Memory<byte>`, `Span<byte>`,
`ArraySegment<byte>`, ...) used to be sized as `Nullable<T>` (8/16 bytes),
so `ldarg`/`ldarga` treated such parameters as small values. The classic
"Object fallback" trace noise (`ParseType VAR ... -> Object (expected in
generic def)`) is benign in comparison; the heuristic gate is what fixed
the parameter-passing cases (`VirtEq(Memory<byte>, Memory<byte>)`).

Also in this commit: `AssemblyLoader.NormalizeGenericInstDefToken` (the
generic-instantiation cache is now keyed on a normalized definition token,
so `Memory`1<byte>` created from different assemblies/tokens unifies to one
MethodTable), the korlib/Debug `Debug.Report` path so JITTest/AppTest
failures are visible on the serial console, and `build/m2-boot-tests.sh`
(boot the deploy image with boot tests enabled and report results).

**Step 2 DONE - socket connection transport + the `WebHost` keep-alive
engine.** `src/ddk/Connections/` ships the Kestrel-shaped surface over
`TcpSocket`: `IConnectionListener`/`SocketConnectionListener` (+ factory),
`ConnectionContext` exposing `Transport` as an `IDuplexPipe`
(`InputReader`/`OutputWriter`), `ConnectionEndPoint` and
`ConnectionAbortedException`. The pumps stay cooperative (driven by the
owner's `Tick()`): socket->app respects Pipe backpressure, app->socket
respects the TCP send window (short sends are re-driven next tick).
`WebHost` now runs entirely on the transport - accepts via the listener,
reads requests from the input pipe, writes responses through the output
pipe with **HTTP/1.1 keep-alive** (persistent by default; `close` for
HTTP/1.0, `Connection: close`, protocol errors, or the per-connection
request cap). Verification on the current image: `JITTest` 3005/0,
`AppTest` 25/25 (incl. the 1 MB echo, `rcvd=1048576`),
`build/webapi-probe.sh` **29/29 PASS** on the M2 engine (echo, CRUD,
keep-alive bursts, parallel requests, guest start/stop/restart) - the probe
has since grown to 31 checks with the M3 raw-client additions below;
`build/sampleapi-probe.sh` 28/28.

Bring-up bugs found while landing step 2 (worth knowing for M3+):

- **Out `bool` parameters miscompile in Tier-0** (stale-slot family): a
  callee that never stored `true` into an `out bool` could have the call
  site read `true` back - every response went out `Connection: close`
  even for clean HTTP/1.1 requests. `WebHost.ParseHead` now returns an
  `int` flags bitmask (bit0 HTTP/1.1, bit1 `Connection: close`) instead
  of two `out bool`s. Keep new inter-method contracts on ints/strings.
- **Server-initiated closes land in `TimeWait`, not `CloseWait`**: the
  transport's peer-FIN detection only covered
  `CloseWait/Closed/Closing/LastAck`, so a `Connection: close` response
  (server FIN first) never set `IsFinished`; connection slots recycled
  only via the 400-tick grace timer, and a burst of >4 sequential
  requests exhausted the 4 slots (accept-then-abort, curl `52`).
  `TimeWait` is now a peer-FIN state; keep-alive connections take the
  `CloseWait` path and release in the same tick.
- **HTTP/1.1 version sniff was off by one** (`line[sp2+5]` is `/`, not
  `1`), silently forcing every response to `close`.
- Pipe/`ReadOnlySequence` layout + cursor normalizations found earlier in
  this milestone are documented at the code sites
  (`PipeSegment` has no fields of its own; `Data`/`Length` live on the
  generic base).

### M3 — HTTP/1.1 core

**Step 1 DONE (this commit) - parser + framing.**

- `src/ddk/Web/HttpParser.cs`: the Kestrel-shaped request-high parser,
  scanning the raw bytes with `byte[]` + index arithmetic (no per-header
  strings; span parameters avoided because Tier-0 passes them as 16-byte
  structs on this every-request path). One linear rescan per tick over the
  bounded 4 KiB request buffer replaces the M1/M2 string-scanning
  `ParseHead`: request line + `Content-Length`, `Connection` token,
  `Transfer-Encoding: chunked`, `Expect: 100-continue`. Flags return as an
  `int` bitmask (the M2 out-bool hazard), positions as out ints.
- **Chunked request bodies** (`TryDecodeChunked`): incremental decode into
  the per-connection char scratch; `413` on overflow (raw buffer full or
  decoded larger than the cap), `400` on bad framing. One trailer line
  supported; chunk extensions not (both documented in the file).
- **`Expect: 100-continue`**: the engine writes the interim
  `HTTP/1.1 100 Continue` through the output pipe once per request when the
  body is not yet fully buffered, then keeps the normal body path.
- **Feature subset**: `IHttpRequestFeature`/`IHttpResponseFeature`
  (method/path/query/body; status/content-type/body + write surface) are
  implemented by `HttpRequest`/`HttpResponse` and exposed as
  `ctx.RequestFeature`/`ctx.ResponseFeature`.
- Verification: `JITTest` 3005/0, `AppTest` 25/25 (1 MB echo),
  `webapi-probe` **31/31** (29 + `build/m3-http-check.py` raw-client
  checks: chunked POST and a staged 100-continue exchange),
  `sampleapi-probe` 28/28.

**Step 2 DONE - async `RequestDelegate`.** `RequestHandlerAsync`
(`delegate Task RequestHandlerAsync(HttpContext)` - the Kestrel-shaped
RequestDelegate) plus `Map*` overloads; WebHost runs the handler, and when
it suspends it stores the returned task + the response on the connection
slot and writes the deferred response on a later service tick once the task
completes (resume runs the state-machine continuation inline at the
completing call, e.g. `/api/v1/poke` completing the `/api/v1/wait` signal -
exercised by `build/m3-http-check.py async`: two raw connections, wait then
poke). Landing this exposed and fixed a **korlib state-machine lifetime bug**:
`AsyncTaskMethodBuilder`/`<T>` boxed the state machine in
`AwaitOnCompleted`/`AwaitUnsafeOnCompleted` BEFORE materializing the task
(`Task` property was lazily created), so the caller's `builder.Task` (read
after `Start` returns) was a different instance than the one the boxed
builder completed at `SetResult` - a suspended `async Task` method never
completed its caller-visible task. The builders now force `_ = Task` before
boxing, and `AsyncValueTaskMethodBuilder<TResult>.SetResult` completes the
backing task when it was materialized. JITTest gained
`Task/suspended-before-signal`-style coverage (`TestTaskSuspension`, the
first on-device test of a Task that suspends and is completed later).

**Step 3 DONE - feature collection + `Expect:` edge semantics + chunked trailers.**

- `HttpFeatureCollection` (the `IFeatureCollection` shape: `TFeature? Get<TFeature>()`
  / `void Set<TFeature>(TFeature?)`) now hangs off `HttpContext.Features`; lookups
  compare Type tokens (`typeof`) and cast only after the token matched. The shape
  is pinned on-device by the new `Web/Features` JITTest category - 6 records:
  Get/Set round-trips for both features, clear-to-null via
  `Set<IHttpResponseFeature>(null)`, and write-through from the feature view to
  the concrete request/response objects.
- `Expect:` semantics: an HTTP/1.1 request with an unsatisfiable `Expect` token
  (anything other than `100-continue`) now gets an immediate
  `417 Expectation Failed`; the `100-continue` interim path is unchanged (one
  interim per request, written only while the body is still pending).
- Chunked trailers: up to 16 trailer lines after the `0` chunk are consumed and
  discarded (parser-side `MaxTrailerLines` bound; content ignored); malformed
  trailer framing still yields `400`.
- Verification: `JITTest` **3030 passed** / 5 known failures (all feature records
  green), `AppTest` 25/25 (1 MB echo), `webapi-probe` **34/34** (32 +
  `expect417` + `trailers` raw-client checks in `build/m3-http-check.py`),
  `sampleapi-probe` 28/28.

Bring-up note (Tier-0): the dispatch's `typeof(TFeature) == typeof(IHttpRequestFeature)`
compiles to a `call System.Type::op_Equality` MemberRef - which was NOT in the AOT
method registry, so compiling the DDK `Get` body failed and the failure cascaded
(`Unknown opcode 0x28` in the DDK compile -> MethodSpec unresolvable ->
`ldftn: failed to resolve token` -> the whole JITTest assembly aborted). Fixed
kernel-side: `Type.op_Equality`/`op_Inequality` are registered in
`AotMethodRegistry.RegisterTypeMethods()` against a handle-based helper - `typeof`
assembles a fresh `RuntimeType` wrapper per call, so the underlying MethodTable
handle (not the wrapper reference) is the type identity. The `ldtoken !0` side
needs no extra work: Roslyn encodes it as a TypeSpec whose MVAR is answered from
the live MethodSpec type-argument context during resolution.

**Known issues (made VISIBLE by the JITTest honesty fix below - these were
silently skipped since M2 because the Memory/Pipelines category aborted at
its ReadOnlySequence test):**

- `RunCategory` used to swallow category exceptions via `Debug.Write` (which
  is compiled out of default builds); it now reports + records them, and
  `MemoryPipelineTests.RunAll` wraps each step in `RunStep` so one throwing
  test no longer kills the rest of the category.
- FIXED (this commit): **hardware-trap catch transfers clobbered the CPU
  interrupt frame.** The dispatcher wrote the catch-funclet's resume address
  at `[context->Rsp - 8]`; for an IDT-fault context that slot is the SS field
  of the five-slot interrupt frame (RIP,CS,RFLAGS,RSP,SS), so the returning
  `iretq` loaded a bogus SS selector and #GP'd AT the iretq itself - every
  catch of a hardware trap (`int 4` overflow, #DE, ...) surfaced as a spurious
  NullReferenceException escaping to the outer wrapper. `category/Overflow`
  and `step/readonly-sequence` were permanently red because of this.
  Hardware-fault dispatches now anchor the catch-funclet entry at the Tier-0
  JIT's documented `parentRbp - 0x108` (below all parent locals; parity
  8 mod 16 keeps the funclet's call sites ABI-aligned); software throws keep
  the historical `[Rsp-8]` anchor (their context was captured at a call site,
  not an interrupt frame, and the rest of the suite exercises that path).
- FIXED (this commit): **Span<T>/ReadOnlySpan<T> indexers used a byte
  stride.** The AOT registry bound `get_Item` for every element type to
  `SpanHelpers.GetByteRef` (pointer + index bytes), so a `ReadOnlySpan<int>`
  of [1,2,3,4] summed as 0x02020201 instead of 10 - `Seq/enumerate-checksum`
  and `Seq/single` were red because of this. The resolver now reads the
  span's generic argument from the memberRef TypeSpec and selects
  `get_Item$i2`/`$i4`/`$i8` entries (element-strided helpers) for char/short,
  int/uint/float, and long/double/pointer-sized elements; byte spans keep
  the original entry.
- FIXED (this commit): **`ldfld` of a large value-type field from an inline
  struct on the eval stack.** `w.v.a` (IL: `ldloc w; ldfld Wrap24::v;
  ldfld Triple24::a` where `v` is a 24-byte struct) hit the scalar switch in
  `CompileLdfld`'s large-container path: it loaded only the first 8 bytes,
  discarded the 24-byte container and pushed an `Int32` entry, so the next
  `ldfld` dereferenced the field VALUE as a pointer (NRE at address 5;
  `vt/ctor24`). The large-VT-field case now re-positions the field bytes to
  the top of the container region (`delta = containerBytes -
  alignedFieldBytes`, overlap-safe qword/byte copies) and replaces the
  container entry with `Struct(size)`.
- FIXED (this commit): **generic-instantiation size inflation for value
  types.** `GetFieldTypeSize`'s `GENERICINST VALUETYPE` branch added the
  signature's type-argument sizes ON TOP of `ComputeInstanceSize(definition)`
  even though the definition's field sizes (resolved against the active type
  context) already include embedded arguments. `ValueTaskAwaiter<ReadResult>`
  consequently sized 104 bytes instead of 24 (base 24 + `ReadResult` 80): the
  async state machine's `newobj` temp was zeroed over 104 bytes and the
  awaiter copies read ZEROS - `vt-awaiter/copy(-sees-completion)` and
  `step/pipe-read-wake` failed with "ValueTask not initialized". The branch
  now parses the instantiation's OWN arguments from the signature
  (`ParseTypeFromSignature` -> Materialized MethodTables), sets them as the
  type context, sizes the definition against them and restores the ambient
  context - exact and consistent across every compilation. (Legacy add-args
  compensation retained only when argument parsing fails.)
- FIXED (this commit): **VAR (`0x13`) return types compiled as 8-byte
  register returns.** Every accessor whose return type is the enclosing
  generic type's parameter - `Task<TResult>.get_Result`,
  `TaskAwaiter<TResult>.GetResult`, `ValueTask<TResult>.get_Result` - was
  classified `IntPtr` (or size 0) because `GetReturnKind`,
  `ParseMethodSigReturnType` and `GetValueTypeSigWithSize` only resolved
  MVAR (`0x1E`, method type parameters) from a context; VAR fell into the
  "not currently supported"/default-IntPtr arms. For `TResult=ReadResult`
  (64B) the callee emitted an 8-byte register return while only the first
  qword carried data: `rt/task-result` probes showed `a=1` intact and all
  later fields as stale stack garbage, the boxed-buffer length read 0, and
  the identity-shaped or luck-of-the-garbage probes (`VT/large-result-read-ok`)
  passed intermittently. The three helpers now resolve VAR through the
  active type type-arg context (`GetTypeTypeArgMethodTable`) and report the
  instantiation's true value-type size / `ReturnKind.Struct`; missing
  context keeps the old pointer-sized fallback. This cleared
  `vt-awaiter/getresult-ok`, `Pipe/read-async-returns-flushed-data` (n=0)
  and `Pipe/async-await-sync-completion` (n=2) plus the whole new `rt/*` +
  `diag/*` probe battery (boxed 64B `ReadResult`/`Probe64` round-trips
  through `Task<TResult>.TrySetResult`/`Result`).
- FIXED (this commit): **large value-type arguments through the hidden-buffer
  call shapes.** `CompileCall`'s `totalArgs==2/3 && needsHiddenBuffer`
  branches popped every argument as a single 8-byte register value. With
  large structs on either slot the pops truncated them (and consumed the
  wrong bytes): `_seq.Slice(pos)` passed `pos._integer` (=1) as the receiver
  and `pos._object` as `&arg`, so compiled `ReadOnlySequence.Slice(
  SequencePosition)` dereferenced `[1+0x30]` (CR2=0x31, GDB-confirmed via
  `TaskAwaiter`-style frames -> `get_End` -> `call *rax` ret addr). Both
  branches now pass >8B value types BY POINTER with their eval-stack bytes
  retained, and the deferred LEAs (emitted after the frame allocation)
  point at them (`arg1` at `[RSP+totalAlloc]`, `this` directly above it,
  arg2/arg1/arg0 rel offsets for the 3-arg shape). `seq/getpos` green.
- FIXED (this commit): **`Task<TResult>(TResult)` ctor compiled without the
  type-arg context.** Repeat boots showed `Task<TResult>..ctor`
  (`m=0x0600072A`) compiled ONCE with the type context `[Object]` (the
  "VAR out-of-range -> Object (expected in generic def)" fallback - the
  verbose `[box]` trace printed `mt=0x80054E0 flags=... REF-NOOP`); its
  `box !!TResult` therefore baked a REF-NOOP, so `AsyncTaskMethodBuilder<TResult>`
  completed sync paths through `Task.FromResult` -> `new Task<TResult>(result)`
  - which stored the RAW int into the boxed-`object _result` field.
  `Task<int>.Result` then unboxed `0x15` (CR2=0x1d = 0x15+8, GDB-confirmed
  in `Task_1__get_Result+270`: `mov 0x30(%rax)` then `movslq 0x8(%r8)`),
  the wrongly-entered fault path built an `AggregateException` and
  `async/simple` + `step/pipe-async-sync` NREd. Both entry points now use
  the parameterless ctor + `Task<TResult>.SetResult` - the TCS path that is
  compiled per instantiation and boxes correctly (`rt/task-result*`,
  `diag/p64-all` were green through exactly that shape). `FromResult` kept
  its public behavior, `ProbeSimpleAsync` added as a regression probe
  (`async/simple*`).
- FIXED (this commit): **hardware-fault catch transfers onto a funclet
  entry trampoline** (the last red, `hw/nullderef-catch`). The probe's
  original `s_nullString.Length` never faulted at all - `string.Length`
  goes through `StringHelpers.GetLength`, which null-guards and returns 0
  - so the probe now uses a raw `ldfld` off a null reference. The real
  bug: the ISR's `iretq` restores the FAULT-TIME rsp (ring 0 -> ring 0
  pushes only Rip/Cs/Rflags, so the kernel cannot redirect rsp at the
  iretq). The dispatcher's transfer assumed the funclet would be entered
  at `rbp-0x108` - true for faults in the method body, but code resumed
  from an earlier catch runs at `rbp-0x100`. A second hardware fault in
  the same frame therefore entered the funclet one slot high (its RET
  popped garbage), and planting the resume slot at `rbp-0x108` from the
  dispatcher clobbered the CPU interrupt frame's Rflags slot (it sits at
  `faultRsp-0x10` when `faultRsp = rbp-0x100`), so the iretq itself #GPed
  (verbose trace: `[EHDisp] mt=NRE rip=0x8001186` - the iretq - after
  both catches had already matched and transferred). Hardware-fault catch
  transfers now land on `exception_funclet_entry_trampoline`
  (native.asm): it runs after the iretq, sets `rsp = rbp-0x108`
  (canonical entry, parity 8 mod 16), plants the resume slot and jumps to
  the handler - so the funclet entry is identical regardless of where in
  the method the fault happened (`tramp=Y` in the verbose `[EHXfer]`
  trace).
- Current boot: `JITTest` **3113 passed / 0 failed**, `AppTest` 25/25
  (1 MB echo); `build/m4-hosting-probe.sh` 14/14; `build/webapi-probe.sh`
  34/34; `build/sampleapi-probe.sh` 28/28. No known reds remain. (Note:
  the boot suites are sensitive to the QEMU CPU model - run with
  `-cpu max` like every other harness; a bare qemu64 default aborts the
  Arithmetic JITTest category with a bare Exception.)
- FIXED (this commit): **large-`TResult` task storage**. `Task<TResult>`/
  `ValueTask<TResult>` used to store the result in typed `TResult?` fields -
  for a ~48-byte `ReadResult` the generic layouts misbehaved on Tier-0
  (minimal probe NREd; the whole pending-`Pipe.ReadAsync` path broke). The
  results are now stored BOXED in `object?` fields, keeping both the generic
  class instance and the ValueTask struct fixed small sizes for every
  `TResult`. The `vt/*` probe steps, the pending-read record and the 1 MB
  pipe pump pass with this in; type-level casts replace the typed reads.
- Fixed along the way (already in): ReadOnlySequence `Length` read the
  cumulative `RunningIndex` contract while the JITTest built segments with
  index 0 (test now chains them properly); `Slice`/`TryGet` recovered
  segments via object->`ReadOnlySequenceSegment<T>` casts which are broken
  on Tier-0 in EVERY shape (as/castclass/is - probed) - they now recover by
  reference identity against the chain (no cast); the sequence `Enumerator`
  copied the whole ~48-byte sequence struct by value (garbled - enumeration
  yielded nothing) and now copies primitives field-by-field.

### M4 — Hosting layer (DONE)

- **`KestrelServer`-lite (`KestrelServerOptions`)**: the engine limits that
  were compile-time constants (MaxConnections/MaxRequestBytes/MaxRoutes/
  MaxPathSegments/MaxRequestsPerConnection/KeepAliveTimeoutMs) plus the
  endpoint Port are now a configuration object; `WebHost` clamps and
  freezes them at construction. `builder.Kestrel.Port = ...` before
  `Build()`.
- **DI shim (`ServiceCollection`)**: instance registrations by service
  type (`builder.Services.AddSingleton<TService>(instance)`, replace on
  re-register) + `app.Services.GetService/GetRequiredService<T>()`
  (class-constrained; typeof-token compare + cast, the pattern the
  feature collection pinned). No factory/impl-type registrations -
  documented limitation.
- **Logging shim (`NeutrinoOS.DDK.Logging`)**: `ILogger`/`ILoggerFactory`
  + `LogLevel` with a `ConsoleLogger` (renders `[category] Level: msg`,
  keeps `LastMessage`) and `NullLogger`/`NullLoggerFactory`.
- **`WebApplicationBuilder` fidelity**: `WebApplication.CreateBuilder(args)`
  returns the builder; `builder.Build()` freezes options + services into
  the application. `--urls <value>` / `--urls=<value>` binds the port
  (scheme/host ignored - the engine binds all interfaces); bare port and
  invalid values keep the default. The M1 fused shape (`webapi` sample)
  migrated to the builder shape unchanged in behavior.
- **`app.Run()`**: hosted mode (`webhost <app.dll>`) starts the listener,
  registers with `WebAppRuntime` and returns - the kernel idle hook then
  drives `TickAll()` (registered as the "webapp" service) so the shell
  stays usable; `webhost stop` runs `StopAll()`. Foreground mode (direct
  `run app.dll`, or `Run(maxTicks)` for tooling) pumps bounded slices
  until `Stop()`.
- **Deployment model**: `src/apps/<name>/Program.cs` builds into
  `/root/appsbin` (p5-apps-build) and deploys to `/apps/webapp/<name>.dll`
  (p5-deploy; the base image already ships `::/apps`, so the deploy
  pre-checks with `mdir` - mtools' `mmd` loops on its overwrite prompt
  with closed stdin, which hung a deploy once). Launched from the shell:
  `webhost /apps/webapp/webapp.dll --urls http://0.0.0.0:8090 &`. The
  kernel path: `Kernel_ServiceStartApp` export -> `ServiceRegistry.StartApp`
  (compiles `WebAppRuntime.SetHosted/GetHostedCount/ClearHosted/TickAll/
  StopAll` from the DDK by name) -> `AssemblyRunner.RunOnCurrentStack`
  loads + JITs + invokes the app's `Main(string[])` on the webhost
  utility's big stack -> count check -> kernel service registration.
- **Sample**: `src/apps/webapp` (builder + `--urls` + DI + logger +
  `/`,`/health`,`/urls`,`/di`,`/log` endpoints); `build/m4-hosting-probe.sh`
  boots the deploy image, launches it through hostfwd (18090->8090) and
  runs 14 checks (launch, logging, HTTP matrix, keep-alive/concurrency,
  `webhost status` = 1 hosted app, `webhost stop`).
- Note: hosted apps share one `webapp` kernel service slot (TickAll/StopAll
  tick/stop all registered apps); `webhost stop` also stops the built-in
  WebService.

### M5 — Compatibility and transports (DONE)

- **TLS 1.3 for the Kestrel engine**: `SocketConnectionContext` gained a
  TLS mode - `EnableTls(certDer, keySeed)` wraps the raw socket in the
  existing `Tls13Connection` (server role). PumpInput drives the
  handshake (`Step()`) before the raw loop and then decrypts records
  into the pipe; PumpOutput encrypts and stays idle until the handshake
  completes; normal completion sends `close_notify` then FIN
  (`SendCloseNotify()` + `Shutdown()`; a hard close would race the
  alert/FIN off the wire). `SocketConnectionListener` takes optional
  cert+seed and enables TLS per accept; `KestrelServerOptions` gained
  `HttpsEnabled`/`HttpsPort` (default 8443). Key material comes from the
  existing `WebService` self-signed store (`WebService.EnsureCertificate`).
  ALPN negotiates `h2` when offered, else serves http/1.1 over TLS.
- **HTTP/2 bridge**: the DDK's existing h2 engine (`Http2Connection`,
  which has driven the built-in WebService since the phase-5 stack) now
  talks through an `IH2Owner` interface — `H2Read/H2Write/H2PeerIp/
  H2BuildResponse`. The built-in `WebConnection` implements it (explicit
  impls, unchanged wire behavior) and the Kestrel engine implements it
  via a `WebHost` bridge. `WebHost` accepts h2 two ways: ALPN `h2` on
  TLS connections, and h2c prior knowledge (the `PRI * HTTP/2.0` preface
  sniffs on cleartext). Responses come from `WebHost.TryBuildH2Response`
  — same route table/DI/handlers as http/1.1, sync handlers only (an
  async handler that doesn't complete synchronously answers 503 over h2);
  h2 requests count in `_requests`.
- **h1 route-matcher regression fixed**: the M4→M5 extraction briefly
  used a 4-`out`-parameter `MatchRoute`; the Tier-0 JIT delivered a
  stale `out` slot (`pathMatched` came back true for unmatched paths),
  turning unknown-route 404s into 405s. The matcher now returns the
  route index and stashes results in `_match*` fields — multi-`out`
  calls are deliberately avoided in this engine.
- **Compat adoptions** (each replaces a shim): `IServiceProvider` shape
  (`app.Services` is a typed `IServiceProvider` with non-generic
  `GetService(Type)`), and `IHostApplicationLifetime`/`HostLifetime` —
  `app.Lifetime` exposes `ApplicationStarted`/`Stopping`/`Stopped`
  tokens + `StopApplication()`; `WebApplication.Start/Stop` notify them
  and on-device `Register(Action)` hooks fire.
- **Sample / tests**: webapp enables HTTPS (`8091`), gains a `/lifetime`
  route and an `ApplicationStarted` hook (logs
  `lifetime: ApplicationStarted hook fired`); `HostingTests` adds
  `TestM5Compat` (typed provider lookup, inactive-service null,
  lifetime token/hook transitions); `build/m5-transport-probe.sh`
  (hostfwd 18090->8090, 18091->8091) runs 14 checks: launch + HTTPS
  listener + lifetime hook, https `/health` `/lifetime` `/di` + unknown
  route 404, h2-over-TLS ALPN (`http_version=2`, bodies), h2c prior
  knowledge, and a plain-http regression check.
- Boot numbers: JITTest 3119/0 in every probe boot; AppTest 25/25 with
  echo 1048576/1048576.
- Documented limitations: no HTTP/3 endpoint for the Kestrel engine yet
  (h3 stays in the built-in stack); async-over-h2 answers 503; TLS
  output on a full window is retried in bounded passes rather than
  split mid-record.

### M6 — OpenAPI document endpoint (DONE)

- **The ASP.NET Core 10 shape** (docs: "Generate OpenAPI documents"):
  `builder.Services.AddOpenApi()` registers the document services
  (`AddOpenApi("internal")` renames the document;
  `AddOpenApi(OpenApiOptions)` registers a prepared options instance);
  `app.MapOpenApi()` maps the endpoint. Defaults mirror the package:
  document name `v1`, route `/openapi/{documentName}.json`
  (`MapOpenApi(pattern)` overrides it; `{documentName}` fills in), and
  OpenAPI 3.1 JSON (the .NET 10 default; `OpenApiOptions.OpenApiVersion`
  re-selects).
- **Generated at request time from the live route table** (the engine's
  ApiExplorer substitute): one path item per distinct pattern, one
  operation per registered method, `{name}` segments described as
  required string path parameters, and a default 200/OK response.
  Unknown HTTP methods and the document endpoint itself are excluded
  (both real package behaviors). `WebApplication.BuildOpenApiJson()`
  exposes the generator to tooling/tests; `WebHost` carries the plumbing
  (per-route kind + accessors) and both dispatch paths - HTTP/1.1 and
  the shared h2 engine - serve the endpoint.
- **Not ported (documented limitations)**: transformer APIs
  (`AddOpenApi(configure)` delegates, `WithOpenApi`/`WithTags`/`WithName`),
  YAML output, build-time document generation, multiple simultaneous
  documents, and handler parameter/return-type introspection - the
  engine has no ApiExplorer metadata, so the document describes methods,
  paths and path parameters only.
- **Sample/tests**: `TasksWebApp` (the `webapi` kernel service) and the
  M4/M5 `webapp` sample both do AddOpenApi + MapOpenApi, and both serve
  the M5 TLS 1.3 listener with the same routes (webapp 8091, webapi
  8443). JITTest `HostingTests.TestOpenApiCompat` adds 9 records
  (service registration, spec version, info customization, path items,
  path parameters, endpoint self-exclusion, brace balance + closed tail,
  empty-paths document). Probes: webapi 34 -> 45 checks (document body,
  200, content type, path parameter, self-exclusion, valid JSON via
  `json.tool`, HTTPS listener line + `https /api/v1/info` + the https
  document valid, and the two forced-sigalg TLS probes) and m5 14 -> 18
  (document over h1, content type, h2c serves it, valid JSON).
- **Fixed in the M6 follow-up**: the first cut's document ended after the
  `"paths"` close - the root `}` was missing - so every real parser
  rejected it ("expected ',' or '}' after property value" at EOF) while
  the substring-based checks all passed. The probes now parse the served
  document with `python3 -m json.tool` and HostingTests pins
  brace-balance + closed tail + the empty route table shape.
- Boot numbers: JITTest 3135/0; AppTest 25/25 + echo 1048576/1048576;
  webapi-probe 45/45; m5-transport-probe 18/18; m4-hosting-probe 14/14;
  sampleapi-probe 28/28.
- **Schannel TLS adoption**: Windows Schannel clients do not offer
  ed25519 in `signature_algorithms`, so the TLS 1.3 server now carries a
  second scheme - ECDSA P-256 (`EcdsaP256.cs`, Montgomery CIOS + Jacobian
  curve math; vectors pinned by the `Crypto/ECDSA` JITTest category) with
  a P-256 certificate (`X509.BuildSelfSignedP256`). ed25519 remains the
  preference; P-256 is selected only when the client omits 0x0807. The
  ClientHello parse is isolated in `ParseClientHello` (small method, int
  flag word) - the combined parse+handshake frame hit a Tier-0 JIT
  stale-locals miscompile that broke every handshake until the split.
- Note: the webapi probe boots `build/x64/neutrinoos-cli.img`, so run
  `build/rebuild-cli-image.sh` after kernel/DDK changes - it tested a
  stale image once (the document 404'd until the CLI image was rebuilt).

### M7 — ASP.NET Core namespace compatibility (DONE)

- **The claim under test**: someone can create a Web API in ASP.NET Core
  on Windows and the *same code* works on NeutrinoOS. The M7 layer adds
  same-namespace shims in the DDK (`src/ddk/AspNetCore/`):
  `Microsoft.AspNetCore.Builder`, `Microsoft.AspNetCore.Hosting`,
  `Microsoft.AspNetCore.Http`, `Microsoft.Extensions.DependencyInjection`,
  `Microsoft.Extensions.Hosting` and `Microsoft.Extensions.Logging` -
  forwarding to the NeutrinoOS web engine. `src/apps/helloapi/Program.cs`
  is the proof sample; `samples/aspnet-compat/helloapi.windows` compiles
  the *same file* against the real `Microsoft.AspNetCore.App` (dotnet 10)
  and both builds are clean.
- **Implemented surface**: `WebApplication.CreateBuilder/Build/Run`,
  `builder.Services`, `builder.WebHost.UseUrls("http[s]://host:port"` /
  bare port; the https scheme enables the M5 TLS 1.3 listener),
  `MapGet/MapPost/MapPut/MapDelete(pattern, RequestDelegate)` returning
  the endpoint builder, `app.Services`/`ServiceProvider`, `app.Lifetime`
  as `Microsoft.Extensions.Hosting.IHostApplicationLifetime`,
  `HttpContext`/`HttpRequest`/`HttpResponse` wrappers (`Method`, `Path`,
  `QueryString`, `Query[key]`, `RouteValues[key]`, `StatusCode`,
  `ContentType`, `WriteAsync(string)`), the `PathString`/`QueryString`/
  `StringValues` structs with the BCL conversions, DI (`AddSingleton<T>`,
  `GetService<T>`/`GetRequiredService<T>` over **`System.IServiceProvider`**
  - korlib gained that BCL type in M7), and `ILogger`/`ILoggerFactory`/
  `LogLevel` + `LogInformation/...` extensions. `UseUrls` lives in
  `Microsoft.AspNetCore.Hosting` (extension on `IWebHostBuilder`), the
  same layout as the real framework, so the `using` directives match.
- **Not ported (documented limits)**: handler overloads that bind to
  `Delegate` - untyped-parameter lambdas, `() => "text"`, `Results.*`,
  `TypedResults`, parameter/model binding; `MapGroup`, middleware
  (`Use*`), `builder.Configuration/Environment/Host`; structured logging
  (the shim's `ILogger.Log(LogLevel, string)` accepts interpolated
  strings, not format args); factory/implementation-type DI
  registrations (instance singletons only); `IQueryCollection`/
  `IRouteValueDictionary` interfaces (the concrete shapes with the same
  members are provided; storing the interface type in a variable does
  not compile). Later milestones can deepen any of these.
- **Verification**: `build/m7-aspnet-probe.sh` - builds and runs the
  Windows twin in WSL, boots the CLI image, launches the device app via
  `webhost /apps/webapp/helloapi.dll &` (hostfwd 18092 -> 8092) and
  **byte-compares** the four endpoint responses against the twin, plus
  404 parity, content-type and Server header. JITTest category
  `AspNetCore/Compat` pins the layer on-device (builder/UseUrls mapping,
  DI through the MS extensions, route registration via the OpenAPI doc,
  context wrappers, request shapes, struct conversions, lifetime
  interface, logger extensions, and the instance-bound async method ->
  `RequestHandlerAsync` delegate pattern the shim's MapGet relies on).
- **Loader note (kernel)**: the net10 reference pack declares
  `System.IServiceProvider` in assembly **System.ComponentModel**, so DDK
  and app metadata scope TypeRefs for it there; the loader's virtual-
  assembly map (`IsVirtualAssembly`) now also routes `System.ComponentModel`
  to korlib - without it every `GetRequiredService`-style call failed to
  resolve on-device (`ResolveMethod FAILED`), which JIT-failed the app's
  Main. The failure mode is pinned by the `AspNetCore/Compat` tests and the
  probe.
- Boot numbers: JITTest 3135 -> **3172/0**; AppTest 25/25 + echo
  1048576/1048576; m7-aspnet-probe **11/11**; webapi-probe 45/45;
  m5-transport-probe 18/18; m4-hosting-probe 14/14; sampleapi-probe 28/28.
- **Twin workflow**: `cd samples/aspnet-compat/helloapi.windows &&
  dotnet run` serves `http://0.0.0.0:8092` (Windows or WSL); the device
  build of the same source is deployed to `/apps/webapp/helloapi.dll` by
  the standard `build/p5-apps-build.sh` + `build/p5-deploy.sh` chain.

## Verification strategy (every milestone)

- `build/webapi-probe.sh` (host-side HTTP matrix through slirp hostfwd) and
  `build/sampleapi-probe.sh` (regression) must stay green — the API surface
  is frozen at M1 behavior.
- Existing suites must not regress: `build/rest-verify-probe.sh` (webhost,
  40 checks), `build/h2body-probe.sh`, `build/utils-check.sh` sweep.
- New runtime primitives (M2+) get on-device unit-style checks first
  (a JIT-compiled test utility), then the transport/HTTP work builds on top.

## Risks / unknowns

- Depth of async state-machine support in the Tier-0 JIT (M2 tests this
  early; M1 deliberately avoids async).
- `Memory<T>`/pipeline code patterns (structs with generic args, `Unsafe`
  liberal use) may hit JIT parity gaps — budget iteration time.
- GC pressure from pipeline segments/buffers: keep allocations under the
  LOH threshold and prefer pooled paths.
- Nothing here blocks on the M1 sample: it already runs.

## Try M1 today

```
# build (WSL):    bash build/rebuild-cli-image.sh
# boot (WSL):     bash build/sampleapi-vm.sh          # 18080 -> 8080
# in the guest:   webapi start
# from Windows:   curl.exe http://127.0.0.1:18080/api/v1/info
```

Sample README: `docs/samples/webapi/README.md` · Postman:
`docs/samples/webapi/NeutrinoWebApi.postman_collection.json`.
