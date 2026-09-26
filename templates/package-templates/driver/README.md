# Driver package template

1. Build your driver against `NeutrinoOS.Driver.Abstractions` (see
   `docs/PHASE8-DRIVER.md` and `samples/hello-driver`).
2. Put the driver dll next to this manifest (`hellodrv.dll`) and adjust
   the `driver` block: `class` (custom/pci/platform/...), `vendorIds`,
   `deviceIds` (hex strings, `[]` for wildcards where supported) and
   `entryPoint`.
3. Pack:

```bash
npkg-host pack --manifest manifest.json --payload-dir . --out hellodrv.npkg
npkg-host verify hellodrv.npkg
```

The kernel driver host instantiates drivers through the assembly's static
parameterless `Create()` factory.
