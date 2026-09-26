# Web application package template

1. Put the web app dll next to this manifest (`helloweb.dll`).
2. Pack:

```bash
npkg-host pack --manifest manifest.json --payload-dir . --out helloweb.npkg
```

The `web` capability marks the package for the web tooling; static
content served by the DDK WebService lives under `/var/www` (see
`docs/PHASE6-WEB.md`).
