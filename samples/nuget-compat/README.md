# NuGet / third-party library samples (M8)

The claim: **real NuGet packages compiled for .NET 10 can be used by
NeutrinoOS apps** — the same `Program.cs`, the same packages, the same
bytes out of the HTTP endpoints on Windows and on the device.

- `libapi.windows/` — a Windows/WSL project that compiles the device
  app's `Program.cs` (`src/apps/libapi/Program.cs`) unchanged against the
  real `Microsoft.AspNetCore.App`, with the same two packages:
  - **Microsoft.Extensions.ObjectPool 10.0.13** (`lib/net10.0` asset)
  - **Microsoft.Extensions.Primitives 10.0.13** (`lib/net10.0` asset)
- The device side is built by `build/p5-apps-build.sh` (reading
  `src/apps/libapi/packages.txt`) into `/root/appsbin/libapi.dll`; the
  package dlls ship to `::/lib` via `build/p5-deploy.sh`, where the
  kernel loader resolves them by assembly name (`/lib/<name>.dll`).

## Run the twin

```bash
cd samples/nuget-compat/libapi.windows
dotnet run                      # listens on http://0.0.0.0:8098
curl http://127.0.0.1:8098/strings
curl http://127.0.0.1:8098/pool
```

## Run the device build

```
webhost /apps/webapp/libapi.dll &
curl http://127.0.0.1:8098/strings   # from the guest; or hostfwd from Windows
```

The first request to each endpoint pays the on-demand JIT compilation of
the Kestrel request pipeline (device-side), so allow generous timeouts
on a cold boot.

## Verify both at once

```bash
bash build/m8-nuget-probe.sh
```

boots the CLI image, launches the device app, runs the Windows twin in
WSL and **byte-compares** the responses of the endpoints.

Policy and findings (what packages work, what the device supports of
the BCL) are in `docs/NUGET.md`.
