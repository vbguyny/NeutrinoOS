# hello-driver (virtual LED)

Minimal `IDriver` sample: matches `platform/led0`, maps its MMIO register,
lights the LED, exposes `/dev/led0` and logs through `IDriverServices`.

## Build

```bash
dotnet build hello-driver.csproj -c Release
# -> bin/Release/net10.0/leddrv.dll
```

## Package and install

Driver packages are built from a manifest (class/vendor/device ids +
entry dll):

```bash
npkg-host pack --manifest manifest.json --payload-dir bin/Release/net10.0 --out hello-led.npkg
```

On the device:

```text
neutrinoos> npkg install hello-led.npkg
neutrinoos> ls /drivers
```

See `docs/PHASE8-DRIVER.md` for the driver lifecycle and
`docs/SDK-PACKAGING.md` for signing and repositories.
