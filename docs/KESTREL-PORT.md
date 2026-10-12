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
- Current boot: `JITTest` **3055 passed / 5 failed**, `AppTest` 25/25
  (1 MB echo). Remaining reds: `vt-awaiter/getresult-ok` and
  `Pipe/read-async-returns-flushed-data` (n=0) / `Pipe/async-await-sync-completion`
  (n=2) - `ReadResult` round-trips through the task-result path still lose
  their tail fields/data in some shapes - plus the long-standing
  `seq/getpos` (NRE) and `hw/nullderef-catch` (silent resume-past-the-#PF
  with no dispatch trace - separate, still-open bug).
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

### M4 — Hosting layer

- `KestrelServer`-lite (endpoint/limits configuration), a minimal
  `IServiceProvider`/DI shim and `ILogger` no-op shim, `WebApplicationBuilder`
  fidelity (`builder.Build()`, options binding for a `--urls`-style flag),
  and the deployment model from `specs/phase-6.md`: a web app DLL under
  `/apps/webapp/` launched by `webhost <app>.dll &` as a background service
  (needs the background-process/`&` support or an equivalent service
  registration path).
- `app.Run()` gains real meaning: a bounded foreground loop that pumps the
  stack (safe because the app owns its sockets), or the cooperative default
  when hosted by the kernel.

### M5 — Compatibility and transports

- Track toward compiling larger slices of the real ASP.NET Core sources
  against korlib (feasibility gate: generics/structs in the JIT, GC
  pressure, thread pool) — each adoption replaces a shim.
- TLS for Kestrel endpoints by bridging the existing TLS 1.3 stack;
  then HTTP/2 and HTTP/3 endpoints reusing the DDK's existing h2/h3 work.

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
