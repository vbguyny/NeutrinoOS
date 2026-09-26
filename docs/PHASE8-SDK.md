# Phase 8 — Developer SDK and Tooling

Everything a third-party developer needs to **write, test, package and
publish** NeutrinoOS applications, utilities, drivers and libraries from
Windows 11 (or any .NET 10 host).

---

## 1. What ships

| Component | Where | Status |
|---|---|---|
| Host `npkg-host` tool (keygen, pack, sign, verify, list, repo-index, publish) | `sdk/npkg/` | complete |
| Repository server `npkg-repo-server` + `scripts/start-repo-server.ps1` | `sdk/repo-server/` | complete |
| `dotnet new` templates: console, utility, driver, webapp, library | `templates/` | complete (all five) |
| MSBuild integration: `.npkg` auto-packaged on Release builds | `sdk/build/NeutrinoOS.App.{props,targets}` | complete |
| Guest API surfaces: DDK (`ProtonOS.DDK.dll`), driver abstractions, packaging | `src/ddk/`, `src/lib/` | complete |
| Samples: hello-console(+library), file-utility, hello-driver, hello-webapp | `samples/` | complete, all build |
| CI templates: GitHub Actions, GitLab CI | `sdk/ci/` | complete |
| SDK archive (downloadable) | `build/p8-sdk-archive.sh` → `dist/neutrinoos-sdk-<ver>.tar.gz` | complete |
| Docs: getting started, packaging, testing, CI/CD, API reference, phase notes | `docs/SDK-*.md`, this file | complete |

## 2. The developer workflow

```powershell
dotnet new neutrino-console -n MyApp
cd MyApp
dotnet build -c Release          # -> bin/Release/net10.0/MyApp.npkg
npkg-host sign MyApp.npkg $env:USERPROFILE\.neutrinoos\private.key
npkg-host publish MyApp.npkg --repo C:\repo
powershell scripts\start-repo-server.ps1 -RepoDir C:\repo -Port 8080
```

```text
neutrinoos> npkg repo add local http://10.0.2.2:8080
neutrinoos> npkg update
neutrinoos> npkg install MyApp
neutrinoos> MyApp
```

Guides: `SDK-GETTING-STARTED.md`, `SDK-PACKAGING.md`, `SDK-TESTING.md`,
`SDK-CICD.md`, `SDK-API-REFERENCE.md`.

## 3. Design decisions

* **Package tooling is shared, not reimplemented.** The host CLI, the
  repository server and the on-device `npkg` compile the same
  `NeutrinoOS.Packaging` sources (and the DDK Ed25519 implementation), so
  indexes, signatures and packages are byte-for-byte identical across
  all three (verified by the repo e2e parity checks).
* **Templates are self-contained projects.** A created project has no
  external package dependencies — the SDK props/targets
  (`neutrino-app.props`/`.targets` ship inside the project) and the two
  `--entry/--files` pack modes are enough, so `dotnet build` works as
  long as `npkg-host` is reachable.
* **Application model:** console/utility/webapp projects compile against
  the standard BCL; the device resolves `System.*` to korlib. Libraries
  referenced by an app are auto-included in its package payload;
  shared libraries are shipped as `/lib` packages.
* **Drivers keep the manifest flow** (class/vendor/device ids + entry
  dll) — `docs/PHASE8-DRIVER.md`.

## 4. Verification

| Check | Script | Result |
|---|---|---|
| Templates install, create, Release-build, auto-package, verify | `build/p8-t4-sdk-e2e.sh` | **ALL-PASS (15 checks)** |
| Repo server: index/signature/public key/package over HTTP, byte-parity with `npkg-host repo-index` | `build/p8-t4-repo-e2e.sh` | **ALL-PASS (12 checks)** |
| Samples: five projects build, payload inclusion, manifest driver pack | `build/p8-t4-samples-e2e.sh` | **ALL-PASS (19 checks)** |
| SDK archive assembles (tools + libs + templates + samples + ci + docs) | `build/p8-sdk-archive.sh` | `dist/neutrinoos-sdk-1.0.0.tar.gz` |

All three run as legs of `tests/run-phase8-tests.ps1` (`sdk`, `repo`,
`samples`, `archive`).

## 5. Known limitations

* `korlib.dll` cannot be built standalone (it is a NoStdLib source
  library); the SDK ships the DDK, driver abstractions and packaging
  assemblies, and applications use the BCL-compiled model.
* The webapp template targets the desktop ASP.NET Core surface; the
  on-device hosting story is the DDK WebService (`webhost`), see
  `PHASE6-WEB.md`.
* `npkg-repo-server` speaks HTTP/1.1 only (no TLS) — front it with a
  reverse proxy for public hosting.
* Repository signing keys are Ed25519 seeds stored as 64-hex files;
  hardware-backed keys are out of scope for Phase 8.
