# Library package template

Libraries install into `/lib` and are loaded on demand by applications.

1. Put the library dll next to this manifest (`hellolib.dll`).
2. Pack:

```bash
npkg-host pack --manifest manifest.json --payload-dir . --out hellolib.npkg
```

Applications that `ProjectReference` a library automatically include it
in their own package payload; ship a separate library package when
several apps share it.
