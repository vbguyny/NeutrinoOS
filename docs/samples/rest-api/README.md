# Sample REST API (`sampleapi`)

A small C# sample that shows how to build a **REST API that runs on NeutrinoOS**
under QEMU — and how to call it from the **Windows 11 host** with `curl`, PowerShell,
or the bundled **Postman collection**.

The API is a task tracker: read/write info, echo query strings and request bodies,
and full CRUD over a small in-memory task store. It listens on **guest port 8080**
and is exposed to Windows as **`http://127.0.0.1:18080`**.

> **See also:** [`docs/samples/webapi`](../webapi/README.md) — the same API written
> in the `WebApplication` / `MapGet` style (Kestrel-port Milestone 1; plan:
> [`docs/KESTREL-PORT.md`](../../KESTREL-PORT.md)).

| File | What it is |
|------|------------|
| `src/ddk/Services/SampleApi.cs` | The service itself (HTTP/1.1 server + router + JSON). Runs inside the kernel's cooperative service model. |
| `src/utilities/sampleapi/Program.cs` | The `sampleapi` shell utility: `start`, `stop`, `status`. |
| `docs/samples/rest-api/NeutrinoSampleApi.postman_collection.json` | Postman v2.1 collection for every endpoint. |
| `build/sampleapi-vm.sh` | Boots the CLI image in QEMU with `18080 -> 8080` forwarded. |
| `build/sampleapi-probe.sh` | Automated end-to-end verification (28 checks). |

## Endpoint reference

| Method | Path | Body / query | Success | Errors |
|--------|------|--------------|---------|--------|
| GET | `/` | – | `200` service blurb + endpoint list | – |
| GET | `/api/v1/info` | – | `200` `{"service","version","uptime_s","requests","tasks"}` | – |
| GET | `/api/v1/echo` | `?msg=...` | `200` `{"method":"GET","msg":"..."}` | – |
| POST | `/api/v1/echo` | any raw body | `200` `{"method":"POST","content_length":N,"you_sent":"..."}` | `413` body > ~4 KB |
| GET | `/api/v1/tasks` | – | `200` `{"count":N,"tasks":[{...}]}` | – |
| POST | `/api/v1/tasks` | `{"name":"..."}` or `name=...` | `201` `{"id":N,"name":"..."}` | `400` no name · `409` store full (8 tasks) |
| GET | `/api/v1/tasks/{id}` | – | `200` `{"id":N,"name":"..."}` | `404` missing |
| PUT | `/api/v1/tasks/{id}` | `{"name":"..."}` | `200` updated task | `400` no name · `404` missing |
| DELETE | `/api/v1/tasks/{id}` | – | `200` `{"deleted":N}` | `404` missing |
| * | `/api/v1/tasks[/{id}]` | – | – | `405` method not allowed |
| * | anything else | – | – | `404` `{"error":"not found"}` |

Responses are `application/json` with `Connection: close`.

## Run it under QEMU

Build once (from Windows, WSL handles the toolchain):

```powershell
wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/rebuild-cli-image.sh
```

### Option A — QEMU in WSL2 (recommended)

```bash
wsl -d Ubuntu-24.04 -u root
bash /mnt/d/Projects/Code/NeutrinoOS/build/sampleapi-vm.sh
```

The image auto-configures `eth0`; when the shell prompt appears, type:

```text
sampleapi start
```

`build/sampleapi-vm.sh` forwards **guest 8080 → host 18080**. WSL2 mirrors
listening ports onto Windows automatically, so **Windows 11 can use
`http://127.0.0.1:18080` directly** — no extra configuration.

### Option B — VirtualBox CLI VM (`scripts/cli-vm.ps1`)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\cli-vm.ps1 -Rebuild
```

The VM (`NeutrinoOSCli`) now includes a NAT forward `sampleapi,tcp,,18080,,8080`;
type `sampleapi start` in the VM window and use the same URL from Windows.
If you have an older VM, add the rule once while it is running:

```powershell
& "$env:ProgramFiles\Oracle\VirtualBox\VBoxManage.exe" controlvm NeutrinoOSCli natpf1 "sampleapi,tcp,,18080,,8080"
```

### Manage the service in the VM

```text
sampleapi start     # start (driven by the kernel idle tick)
sampleapi status    # running / not running
sampleapi stop      # stop and free the service slot
```

## Call it from Windows 11

```powershell
# PowerShell
Invoke-RestMethod http://127.0.0.1:18080/api/v1/info
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:18080/api/v1/tasks `
    -ContentType 'application/json' -Body '{"name":"try the sample"}'
Invoke-RestMethod http://127.0.0.1:18080/api/v1/tasks
```

```cmd
:: curl.exe (bundled with Windows 11)
curl.exe http://127.0.0.1:18080/api/v1/info
curl.exe -X POST -H "Content-Type: application/json" -d "{\"name\":\"hello\"}" http://127.0.0.1:18080/api/v1/tasks
curl.exe http://127.0.0.1:18080/api/v1/echo?msg=from-windows
```

## Postman

1. Import `docs/samples/rest-api/NeutrinoSampleApi.postman_collection.json`.
2. The collection variable `baseUrl` defaults to `http://127.0.0.1:18080`.
3. Send requests individually, **or** use *Run collection* to execute all of
   them in order. The *Create task* request captures the new id into the
   `taskId` variable, which the subsequent Get / Update / Delete / 404-check
   requests use — so a full run always exercises the whole CRUD chain.

It also includes an *Error cases* folder (400 / 404 / 405) so you can watch the
status-code semantics, and every request carries `pm.test` assertions.

## How it works

NeutrinoOS services are **cooperative**: a service is a plain C# class in the
DDK assembly with `Start()` / `Tick()` / `Stop()`. The kernel's `ServiceRegistry`
JIT-compiles it from the DDK, calls `Start()` when the utility asks, and calls
`Tick()` from the shell idle loop. There are no threads; each `Tick()` does a
bounded slice of work.

`SampleApi.Tick()`:

1. `NetworkPump.Pump(stack, 4)` — move packets between the virtio NIC and the stack.
2. `listener.Pending()` / `Accept()` — pick up new TCP connections (max 4 open).
3. For each connection: non-blocking `Receive()` into a 4 KB buffer, wait for
   the full request (head + `Content-Length` body), parse the request line and
   `Content-Length` header, then route.
4. `NetworkPump.FlushTx(stack)` — push queued responses out.

`Route()` is a short if-chain over method + path that writes simple JSON with
DDK helpers (`IntToStr`, `StrEq`, `TrimStr`) and a small `JsonEscape`. The task
store is two parallel arrays (id → name, capped at 8 tasks).

## Make your own API

1. Copy `src/ddk/Services/SampleApi.cs` to e.g. `MyApi.cs` in the same folder
   (keep the `NeutrinoOS.DDK.Services` namespace) and rewrite `Route()`.
2. Register the name in `src/kernel/Services/ServiceRegistry.cs`
   (`"myapi"` → `"MyApi"`; max 4 concurrent services).
3. Add a utility `src/utilities/myapi/Program.cs` (copy `sampleapi`'s) and add
   `"myapi"` to `ExternalUtilityNames` in `src/kernel/Shell/ShellBuiltins.cs`
   and to the `ALL` list in `build/utils-check.sh`.
4. Pick a free guest port (8080 and 80/443 are used by the samples) and rebuild.

The build globs `src/utilities/*`, so a new utility directory is compiled and
deployed automatically.

## Automated verification

```bash
bash build/sampleapi-probe.sh   # in WSL: boots QEMU, runs the 28-check matrix
```

Expected result: `PASSED=28 FAILED=0` (discovery, echo, full CRUD + error codes,
concurrency, host-side access, and guest-side start/stop/restart).
