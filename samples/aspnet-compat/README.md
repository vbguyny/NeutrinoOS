# ASP.NET Core compatibility samples (M7)

The claim: **someone can create a Web API in ASP.NET Core on Windows,
and the same code works on NeutrinoOS.**

- `helloapi.windows/` — a Windows/WSL project that compiles the device
  app's `Program.cs` (`src/apps/helloapi/Program.cs`) unchanged, against
  the **real** `Microsoft.AspNetCore.App` (dotnet 10). There is exactly
  one source file, shared with the device build.
- The device side of the same source is built automatically by
  `build/p5-apps-build.sh` into `/root/appsbin/helloapi.dll` and shipped
  by `build/p5-deploy.sh` to `/apps/webapp/helloapi.dll` on the image.

## Run the twin

```bash
cd samples/aspnet-compat/helloapi.windows
dotnet run                      # or: dotnet build && dotnet bin/Debug/net10.0/helloapi.dll
curl http://127.0.0.1:8092/
curl http://127.0.0.1:8092/health
curl http://127.0.0.1:8092/greet/ada
curl "http://127.0.0.1:8092/echo?msg=hi"
```

## Run the device build

```
# in the NeutrinoOS CLI image (webhost utility):
webhost /apps/webapp/helloapi.dll &
curl http://127.0.0.1:8092/health     # from the guest; or hostfwd from Windows
```

## Verify both at once

```bash
bash build/m7-aspnet-probe.sh
```

boots the CLI image, launches the device app, runs the Windows twin in
WSL, and **byte-compares** the responses of the four endpoints.

The supported API subset and its documented limits are in
`docs/KESTREL-PORT.md` (M7) and `src/ddk/AspNetCore/`.
