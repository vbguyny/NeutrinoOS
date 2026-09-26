# NeutrinoOS SDK samples

Five small, buildable projects that exercise the developer workflow:

| Sample | Type | What it shows |
|---|---|---|
| `hello-library/` | class library | a reusable `hellolib.dll` (\<= 8 chars for FAT), packaged to `/lib` |
| `hello-console/` | application | references `hello-library`; the library dll is auto-included in the app package payload |
| `file-utility/` | shell utility | file I/O (`File.ReadAllLines/WriteAllLines`), args, exit codes; installs to `/bin` |
| `hello-driver/` | device driver | `IDriver` for a virtual LED device; packaged from `manifest.json` via `npkg-host pack` |
| `hello-webapp/` | web app | ASP.NET Core minimal API for the NeutrinoOS web-hosting surface |

Build everything (Release builds also produce `.npkg` packages):

```bash
dotnet build samples/hello-console/hello-console.csproj -c Release
dotnet build samples/hello-library/hello-library.csproj -c Release
dotnet build samples/file-utility/file-utility.csproj -c Release
dotnet build samples/hello-driver/hello-driver.csproj -c Release
dotnet build samples/hello-webapp/hello-webapp.csproj -c Release
```

The host `npkg-host` tool must be on `PATH` (or set `NeutrinoNpkgTool` /
`NEUTRINOOS_NPKG`). See `docs/SDK-GETTING-STARTED.md`.
