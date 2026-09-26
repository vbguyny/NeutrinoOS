# SDK — Packaging, Signing and Repositories

`.npkg` is the NeutrinoOS package format (ZIP container: `manifest.json`,
`checksums.sha256`, `signature.sig`, `payload/...`). Full format and
manifest schema: `docs/PHASE8-NPKG.md`.

The SDK builds packages three ways:

| Way | Who | Output |
|---|---|---|
| MSBuild (automatic) | `dotnet build -c Release` with the SDK targets | `<AssemblyName>.npkg` next to the dll |
| `npkg-host pack` (SDK mode) | scripts / custom pipelines | `--name --version --entry/--files` |
| `npkg-host pack --manifest` | drivers + hand-crafted manifests | manifest-file mode |

---

## 1. Automatic packaging (applications, utilities, libraries)

The SDK props/targets (`NeutrinoOS.App.props`, `NeutrinoOS.App.targets`)
hook `AfterTargets="Build"` for Release builds:

```
dotnet build -c Release
# -> bin/Release/net10.0/MyApp.npkg
```

Overridable properties (set in the csproj or with `/p:`):

| Property | Default | Meaning |
|---|---|---|
| `NeutrinoNpkgTool` | `npkg-host` | path of the host tool |
| `NeutrinoPackageOnBuild` | `true` | disable to skip packaging |
| `NeutrinoPackageName` | `$(AssemblyName)` | package name |
| `NeutrinoArchitecture` | `any` | `any` / `x86-64` / `arm64` |
| `NeutrinoInstallPath` | `apps` (libs: `lib`) | install root |
| `NeutrinoProvides` | `application` | capability tags |
| `NeutrinoSigningKey` | – | Ed25519 private key file (64 hex); signs the package during the build |

Extra payload files (beyond the entry dll and referenced project outputs):

```xml
<ItemGroup>
  <NeutrinoPayload Include="assets\config.json" />
</ItemGroup>
```

Project references are included automatically (`hellocon.npkg` contains
`hellolib.dll`, see `samples/hello-console`).

## 2. Signing

```bat
npkg-host keygen                       :: -> %USERPROFILE%\.neutrinoos\{private,public}.key
npkg-host sign MyApp.npkg %USERPROFILE%\.neutrinoos\private.key
npkg-host verify MyApp.npkg --key %USERPROFILE%\.neutrinoos\public.key
```

Signing during the build:

```bat
dotnet build -c Release /p:NeutrinoSigningKey="%USERPROFILE%\.neutrinoos\private.key"
```

* `keygen` writes the private key (64 hex chars) and public key;
  back the private key up — it is the package's identity.
* The manifest records the **signer fingerprint** = `SHA-256(public key)`.
* On the device, `npkg install` enforces signatures per repository trust
  policy; unsigned packages need `--allow-untrusted`.
* Key format: Ed25519 seed (32 bytes / 64 hex chars). Keep the private
  key out of source control; CI uses a secret (see `SDK-CICD.md`).

## 3. Repositories

A repository is a plain directory:

```
repository.json        signed index (npkg-repo/1)
repository.json.sig    Ed25519 signature over the raw JSON
repo.pub               signing public key (64 hex)
<file>.npkg            packages
```

Build the index + signature:

```bat
npkg-host repo-index --dir repo --key %USERPROFILE%\.neutrinoos\private.key --name myrepo
```

Publish a package into a directory:

```bat
npkg-host publish MyApp.npkg --repo repo
npkg-host repo-index --dir repo --key ...   :: regenerate after publishing
```

### Local repository server

`npkg-repo-server` serves the directory over HTTP and **regenerates the
index on every request** — just copy a package in:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\start-repo-server.ps1 `
    -RepoDir C:\repo -Port 8080 -Key $env:USERPROFILE\.neutrinoos\private.key
```

Device side:

```text
neutrinoos> npkg repo add local http://10.0.2.2:8080     # QEMU user-net host
neutrinoos> npkg update
neutrinoos> npkg search
neutrinoos> npkg install MyApp
```

Repositories can also be local paths (`file:///path` or `/path`) — handy
on the device itself.

**Priorities:** `npkg repo add <name> <url> --priority <n>` (lower number
= preferred, default 50). `npkg repo list` shows the resolution order;
when several repositories carry the same package version, the highest
priority one wins, and an unreachable mirror is skipped with a warning.

**HTTPS:** not available on-device yet (Phase 8 has no TLS client);
`npkg repo add` rejects `https://` with a clear message. Ed25519
signatures + the pinned fingerprint still protect package integrity over
`http://` — for public hosting, terminate TLS at a reverse proxy and
mirror for devices (see `docs/PHASE8-ECOSYSTEM.md`).

## 4. Driver packages (manifest mode)

Driver packages carry a `driver` block (class/vendor/device ids +
entryPoint). See `samples/hello-driver/manifest.json`:

```bat
npkg-host pack --manifest manifest.json ^
               --payload-dir bin\Release\net10.0 ^
               --out leddrv.npkg
npkg-host verify leddrv.npkg
```

Driver lifecycle and the ABI: `docs/PHASE8-DRIVER.md`.

## 5. Inspecting packages

```bat
npkg-host list MyApp.npkg
:: name/version/architecture/install path/provides/entry points/payload
npkg-host verify MyApp.npkg [--key pubkey]
:: [PASS] checksums, [PASS] signature
```
