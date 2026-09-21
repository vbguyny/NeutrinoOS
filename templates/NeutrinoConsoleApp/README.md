# NeutrinoConsoleApp template

Starting point for .NET 10 console applications that run on NeutrinoOS.

## Build

```powershell
pwsh scripts/build-app.ps1 -Project .\NeutrinoConsoleApp.csproj -AppName myapp
```

or directly:

```bash
dotnet build -c Release -f net10.0 -o stage
```

## Deploy

The `run` command reads assemblies from the NeutrinoOS image. Deploy with
`mcopy` (from WSL, on a copy of `build/x64/neutrinoos.img`), then boot
QEMU and launch:

```bash
mcopy -o -i build/x64/neutrinoos.img stage/myapp.dll ::/apps/myapp.dll
# class libraries referenced by the app go to /lib (loaded on demand):
mcopy -o -i build/x64/neutrinoos.img stage/mylib.dll ::/lib/mylib.dll
```

```text
neutrinoos> run /apps/myapp.dll
```

## Constraints

- Keep file/assembly names FAT 8.3-safe (<= 8 characters before `.dll`);
  the FAT driver matches short names only.
- `System.Runtime`, `System.Console`, `System.Linq`, `System.Collections`,
  `System.Threading` and friends resolve to `korlib` at run time; see
  `docs/PHASE4-BCL.md` for the implemented subset and
  `docs/PHASE4-JIT-COMPAT.md` for the IL patterns the Tier-0 JIT handles.
- For kernel/driver access (network stack, debug output) reference
  `src/ddk/DDK.csproj` or `src/lib/ProtonOS.Net/ProtonOS.Net.csproj` the
  way `src/AppTest` does.
