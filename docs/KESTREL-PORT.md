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
  only, no keep-alive, no `app.Run()` blocking loop (the kernel drives
  `Tick()`), no DI/logging surface yet.

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

Remaining M2 work: the transport port (items 2-3 below) - `IConnectionListenerFactory`,
`ConnectionContext`/`IDuplexPipe` over `TcpSocket`, and the `WebHost`
re-implementation with keep-alive, followed by the acceptance probes.

### M3 — HTTP/1.1 core

- Port the shape of Kestrel's `HttpParser`/`Http1Connection` request
  processing onto the M2 pipelines: header parsing (span-based),
  `Content-Length` + chunked bodies, `Expect: 100-continue`,
  request/response feature collection (`IHttpRequestFeature`,
  `IHttpResponseFeature` subset), `RequestDelegate` becomes async.
- Body handling must reuse the M1 semantics (413/431 limits) so all probes
  keep passing unchanged.

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
