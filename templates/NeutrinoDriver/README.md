# NeutrinoDriver — driver template

A starting point for writing a NeutrinoOS driver. Copy this directory
somewhere in your own tree (keeping the `ProjectReference` path valid or
switching it to your checkout of `NeutrinoOS.Driver.Abstractions`), then:

1. Edit `MyDriver.cs` — set `Name`/`Version`, implement `Match()` for your
   hardware and do the bring-up in `Start()` using `IDriverServices`
   (`MapMmio`, `AllocateDma`, `RegisterInterrupt`, `CreateDeviceNode`, `Log`).
2. Edit `manifest.json` — package name/version, the `driver` block
   (`class`, `vendorIds`, `deviceIds`, `entryPoint` = your assembly `*.dll`).
3. Build and package it:

   Windows (from the repository root):

   ```powershell
   pwsh scripts/build-driver.ps1 -Project path\to\NeutrinoDriver.csproj -Key .\my-private.key
   ```

   or manually:

   ```bash
   dotnet build NeutrinoDriver.csproj -c Release
   # stage just the driver assembly (the ABI dll is provided by the OS):
   npkg-host pack --manifest manifest.json --payload-dir <dir with MyDriver.dll> \
       --out example.mydriver-1.0.0.npkg --key /path/to/private.key
   ```

4. Ship it to a device by adding it to a repository (`npkg-host repo-index`,
   or copy the `.npkg` into `/repo` on the image) and install with
   `npkg install example.mydriver` in the NeutrinoOS shell. The package
   lands under `/drivers` (per the manifest's `installPath`); packaged
   drivers under the driver framework are consumed by the driver manager
   as the loading path completes (see `docs/PHASE8-DRIVER.md`).

## Rules of thumb

- Reach hardware **only** through `IDriverServices` — never poke kernel
  internals; that keeps drivers portable to isolated hosts later.
- `Match()` runs against every device: no allocation, no hardware access.
- `Probe()` may validate more (resource presence, variant ids) and must
  return `false` to defer when unsure.
- Keep interrupt handlers short; defer work to a flag or queue drained by
  the normal read/tick path.
- Declare `AbiMajor`/`AbiMinor` from `DriverAbi` so the manager can gate
  compatibility.
- Avoid `bool[]` arrays in kernel-linked code (bflat codegen limitation);
  use `byte[]`.
