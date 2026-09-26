# NeutrinoOS SDK — Getting Started

Build applications, utilities, drivers and libraries for NeutrinoOS from
Windows 11 (or any .NET 10 host: Linux, macOS, WSL).

---

## 1. Install the SDK

**Requirements:** .NET 10 SDK (`dotnet --version` >= 10.0).

### Option A — download the SDK archive

Unpack `neutrinoos-sdk-<version>.tar.gz` (from `dist/`,
`tar -xzf` works on Windows 10+ and Linux):

```
neutrinoos-sdk/
  bin/npkg-host            package tool (Windows/Linux)
  bin/npkg-repo-server     local package repository over HTTP
  lib/                     guest API surfaces (DDK, Driver.Abstractions, Packaging)
  build/                   MSBuild packaging integration
  templates/               dotnet new templates
  samples/                 five buildable samples
  ci/                      CI workflow templates
  docs/                    these guides
```

Add `bin/` to `PATH` (or set `NEUTRINOOS_NPKG` to the full path of
`npkg-host`). Install the templates:

```powershell
dotnet new install .\neutrinoos-sdk\templates
```

### Option B — work from the NeutrinoOS source tree

```bash
bash build/p8-sdk-build.sh                 # builds /root/p8sdk/npkg-host (WSL)
export PATH=/root/p8sdk:$PATH
dotnet new install <repo>/templates --force
```

Templates: `neutrino-console`, `neutrino-utility`, `neutrino-driver`,
`neutrino-webapp`, `neutrino-library`.

## 2. Create a project

```powershell
dotnet new neutrino-console -n MyApp
cd MyApp
dotnet build -c Release
```

Output:

```
bin/Release/net10.0/MyApp.dll     # the assembly
bin/Release/net10.0/MyApp.npkg    # the NeutrinoOS package (auto-packaged)
```

The Release build runs `npkg-host pack` (see `build/NeutrinoOS.App.targets`).
The tool is found on `PATH`, or via the `NeutrinoNpkgTool` MSBuild property /
`NEUTRINOOS_NPKG` environment variable.

> **FAT 8.3:** keep assembly names <= 8 characters when installing onto
> the FAT boot volume (`npkg install` with install path `/apps`, `/bin`).
> Disk-backed installs do not have this restriction.

## 3. Get it onto the device

### 3a. Package install over a repository (recommended)

Serve a directory of `.npkg` files from the host:

```powershell
mkdir $env:USERPROFILE\.neutrinoos\repo
copy bin\Release\net10.0\MyApp.npkg $env:USERPROFILE\.neutrinoos\repo\
scripts\start-repo-server.ps1            # http://<host-ip>:8080
```

On the device (QEMU user networking: host = `10.0.2.2`):

```text
neutrinoos> npkg repo add local http://10.0.2.2:8080
neutrinoos> npkg update
neutrinoos> npkg install MyApp
neutrinoos> MyApp
```

### 3b. Direct copy (development images)

```bash
mcopy -o -i build/x64/neutrinoos.img bin/Release/net10.0/MyApp.dll ::/apps/MyApp.dll
```

```text
neutrinoos> run /apps/MyApp.dll
```

(`run` also accepts `.npkg` files copied onto the image, and apps in
`/apps` are on `$PATH` by name.)

## 4. Program against NeutrinoOS

Console applications compile against the normal .NET BCL; on the device
the kernel resolves `System.Runtime`, `System.Console`, `System.Linq`,
`System.Collections`, `System.Threading`, `System.IO` and friends against
*korlib*. See:

* `docs/PHASE4-BCL.md` — the implemented BCL subset
* `docs/PHASE4-JIT-COMPAT.md` — IL patterns the Tier-0 JIT handles
* `docs/SDK-API-REFERENCE.md` — API reference (korlib subset, drivers, packaging)

Libraries referenced by an app are **automatically included in its
package payload** (any `ProjectReference` output, .dll only). Ship shared
libraries as `neutrino-library` packages (`/lib`) when they are used by
more than one app.

## 5. Utilities and drivers

* `neutrino-utility` templates install to `/bin` (on `$PATH`) and declare
  the `utility` capability:
  `dotnet new neutrino-utility -n mytool`
* `neutrino-driver` packages are built from a driver manifest — see
  `docs/PHASE8-DRIVER.md` and `docs/SDK-PACKAGING.md`.

## 6. Next steps

* `docs/SDK-PACKAGING.md` — signing, repositories, package format
* `docs/SDK-TESTING.md` — testing in QEMU / VirtualBox from Windows 11
* `docs/SDK-CICD.md` — GitHub Actions / GitLab CI templates
* `samples/` — five worked examples (console + library, utility, driver, web app)
