# WebAPI sample (`webapi`) — Kestrel-port Milestone 1

The WebAPI-project version of the task-tracker REST API: **the same endpoints
as `docs/samples/rest-api`, but written in the `WebApplication` / `MapGet`
style that the Kestrel port targets.** This is the shape ASP.NET Core
minimal APIs use, running on NeutrinoOS under QEMU today — and reachable
from Windows 11 at **`http://127.0.0.1:18080`**.

> Port plan and milestones: [`docs/KESTREL-PORT.md`](../../KESTREL-PORT.md).
> M1 ships this app; M2+ replaces the engine underneath it with
> System.IO.Pipelines + the async transport, then the real Kestrel Core
> sources — the app code you see here stays the same.

| File | What it is |
|------|------------|
| `src/ddk/Web/WebApplication.cs` | `WebApplication.CreateBuilder(args)` + `MapGet/MapPost/MapPut/MapDelete` (builder and app fused in M1) |
| `src/ddk/Web/HttpContext.cs` | `HttpContext` / `HttpRequest` / `HttpResponse` / `RequestHandler` / `WebStatus` |
| `src/ddk/Web/WebHost.cs` | The engine: listener, connection slots, head parse + body wait, route table with `{id}` captures, 404/405 semantics |
| `src/ddk/Samples/TasksWebApp.cs` | **The app** — `Build()` wires the routes; ~9 handlers implement the API |
| `src/utilities/webapi/Program.cs` | `webapi start \| stop \| status` |
| `docs/samples/webapi/NeutrinoWebApi.postman_collection.json` | Postman v2.1 collection for every endpoint |
| `build/webapi-probe.sh` | Automated end-to-end verification (29 checks) |

## The app, as you'd write it

```csharp
private static WebApplication Build()
{
    var app = WebApplication.CreateBuilder(new string[0]);
    app.Name = "webapi";
    app.Port = 8080;

    app.MapGet("/", OnRoot);
    app.MapGet("/api/v1/info", OnInfo);
    app.MapGet("/api/v1/echo", OnEchoGet);
    app.MapPost("/api/v1/echo", OnEchoPost);
    app.MapGet("/api/v1/tasks", OnListTasks);
    app.MapPost("/api/v1/tasks", OnCreateTask);
    app.MapGet("/api/v1/tasks/{id}", OnGetTask);       // {id} captured per route
    app.MapPut("/api/v1/tasks/{id}", OnRenameTask);
    app.MapDelete("/api/v1/tasks/{id}", OnDeleteTask);
    return app;
}

private static void OnListTasks(HttpContext ctx)
{
    ctx.Response.Json(TasksJson());                    // 200 application/json
}
```

Compare with `src/ddk/Services/SampleApi.cs` (the previous sample): the same
API in the hand-wired socket style. The WebApplication version keeps the
routing and HTTP plumbing in the framework — handlers are ~40 lines of
business logic.

## Mapping to ASP.NET Core

| Here (M1) | ASP.NET Core |
|-----------|--------------|
| `WebApplication.CreateBuilder(args)` | `WebApplication.CreateBuilder(args)` |
| (builder and app fused) | `builder.Build()` |
| `app.MapGet(pattern, RequestHandler)` | `app.MapGet(pattern, lambda)` |
| `void Handler(HttpContext ctx)` | any `RequestDelegate` |
| `ctx.Request.Query("k")` / `.Route("id")` | `ctx.Request.Query["k"]` / `RouteValues["id"]` |
| `ctx.Response.Json(fragment)` | `Results.Json(...)` |
| kernel drives `Start()/Tick()/Stop()` | `app.Run()` (blocking) |

The `webapi` utility runs the app through the kernel's cooperative service
model (JIT-compiled from the DDK, `Tick()` from the shell idle hook), so the
shell stays usable while the API serves.

## Endpoints

Identical to `docs/samples/rest-api` (only the service identity strings
differ: `/api/v1/info` reports `"service":"webapi"`):

| Method | Path | Success | Errors |
|--------|------|---------|--------|
| GET | `/` | `200` blurb (service, port, style, endpoint list) | – |
| GET | `/api/v1/info` | `200` `{"service","version","uptime_s","requests","tasks"}` | – |
| GET | `/api/v1/echo` | `200` `{"method":"GET","msg":"..."}` (`?msg=`) | – |
| POST | `/api/v1/echo` | `200` `{"method":"POST","content_length":N,"you_sent":"..."}` | `413` > ~4 KB |
| GET | `/api/v1/tasks` | `200` `{"count":N,"tasks":[...]}` | – |
| POST | `/api/v1/tasks` | `201` `{"id":N,"name":"..."}` | `400` no name · `409` store full (8) |
| GET/PUT/DELETE | `/api/v1/tasks/{id}` | `200` task / updated / `{"deleted":N}` | `400` · `404` |
| * | wrong method on a known path | – | `405` (engine route table) |
| * | anything else | – | `404` `{"error":"not found"}` |

## Run it under QEMU

```powershell
# build once (WSL handles the toolchain):
wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/rebuild-cli-image.sh
```

```bash
# boot with guest 8080 forwarded to host 18080:
wsl -d Ubuntu-24.04 -u root
bash /mnt/d/Projects/Code/NeutrinoOS/build/sampleapi-vm.sh
```

In the VM console:

```text
webapi start     # start (driven by the kernel idle tick)
webapi status    # running / not running
webapi stop      # stop and free the service slot
```

## From Windows 11

WSL2 mirrors the forwarded port to Windows automatically:

```powershell
Invoke-RestMethod http://127.0.0.1:18080/api/v1/info
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:18080/api/v1/tasks `
    -ContentType 'application/json' -Body '{"name":"try the webapi sample"}'
Invoke-RestMethod http://127.0.0.1:18080/api/v1/tasks
```

```cmd
curl.exe http://127.0.0.1:18080/api/v1/echo?msg=from-windows
curl.exe -X DELETE http://127.0.0.1:18080/api/v1/tasks/1
```

VirtualBox CLI VM (`scripts\cli-vm.ps1`) users: the `sampleapi,tcp,,18080,,8080`
NAT forward also serves this sample.

## Postman

1. Import `docs/samples/webapi/NeutrinoWebApi.postman_collection.json`.
2. `baseUrl` defaults to `http://127.0.0.1:18080`.
3. *Run collection* executes Info & Echo, the full Tasks CRUD chain
   (Create captures `taskId`, then Get / Update / Delete / 404-check), and
   the error cases with `pm.test` assertions on every request.

## Automated verification

```bash
bash build/webapi-probe.sh   # in WSL: boots QEMU, 29-check matrix
```

Expected: `PASSED=29 FAILED=0` — discovery, echo, CRUD, 400/404/405
semantics, `Server:` header, concurrency, and guest-side
start/stop/restart. The `sampleapi` probe (28 checks) is the frozen
regression suite for the previous sample.
