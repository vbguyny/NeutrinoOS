# Application package template

1. Put your application dll next to this manifest (`helloapp.dll`, or
   rename and update `entryPoints`).
2. Keep the assembly name FAT 8.3 sized (<= 8 chars) when installing onto
   the FAT boot volume.
3. Pack, verify, publish:

```bash
npkg-host pack --manifest manifest.json --payload-dir . --out helloapp.npkg
npkg-host verify helloapp.npkg
npkg-host publish helloapp.npkg --repo /path/to/repo
```

On the device: `npkg install com.example.hello-app` then run `helloapp`
(or `run /apps/helloapp.dll`).
