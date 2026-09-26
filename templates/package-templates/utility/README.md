# Utility package template

Utilities install into `/bin`, land on `$PATH` and are runnable by their
entry-point name.

1. Put your tool dll next to this manifest (`hellotool.dll`).
2. Pack:

```bash
npkg-host pack --manifest manifest.json --payload-dir . --out hellotool.npkg
```

3. Device: `npkg install com.example.hello-tool && hellotool`.
