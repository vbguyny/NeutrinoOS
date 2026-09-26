# NeutrinoOS console application template

Starting point for .NET 10 console applications that run on NeutrinoOS.

```
dotnet new neutrino-console -n MyApp
cd MyApp
dotnet build -c Release
```

The Release build produces `bin/Release/net10.0/MyApp.dll` **and**
`MyApp.npkg` (the NeutrinoOS package) through the SDK MSBuild targets
(`neutrino-app.props` / `neutrino-app.targets` in this folder). The npkg
tool must be on `PATH`, or set `NeutrinoNpkgTool` (MSBuild property) /
`NEUTRINOOS_NPKG` (environment variable) to the full path of `npkg-host`.

## Install and run

Package install (preferred):

```text
neutrinoos> npkg install MyApp.npkg
neutrinoos> MyApp
```

Manual copy (development images, no npkg):

```bash
mcopy -o -i build/x64/neutrinoos.img bin/Release/net10.0/MyApp.dll ::/apps/MyApp.dll
```

```text
neutrinoos> run /apps/MyApp.dll
```

## Constraints

- Keep file/assembly names FAT 8.3-safe (<= 8 characters before `.dll`)
  when installing onto the FAT boot volume; the FAT driver matches short
  names only.
- `System.Runtime`, `System.Console`, `System.Linq`, `System.Collections`,
  `System.Threading` and friends resolve to `korlib` at run time; see
  `docs/PHASE4-BCL.md` for the implemented subset and
  `docs/PHASE4-JIT-COMPAT.md` for the IL patterns the Tier-0 JIT handles.
- Class libraries referenced by the app go to `/lib` (loaded on demand);
  ship them as library packages (`neutrino-library` template).

Full walkthrough: `docs/SDK-GETTING-STARTED.md`.
- For kernel/driver access (network stack, debug output) reference
  `src/ddk/DDK.csproj` or `src/lib/ProtonOS.Net/ProtonOS.Net.csproj` the
  way `src/AppTest` does.
