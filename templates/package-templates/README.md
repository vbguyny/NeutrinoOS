# Package templates

Ready-to-adapt `manifest.json` files for the five common package types.
Each subdirectory holds a manifest plus a README explaining what to drop
in and how to pack it with the host `npkg-host` tool:

| Template | Installs to | `provides` | Payload |
|---|---|---|---|
| `application/` | `/apps` | `application` | one app dll (entry point) |
| `utility/` | `/bin` | `utility` | one tool dll (on `$PATH`) |
| `driver/` | `/drivers` | `driver` | driver dll + `driver` block |
| `library/` | `/lib` | `library` | shared library dll |
| `web-app/` | `/apps` | `application`, `web` | web app dll |

Quick loop (from a template directory):

```bash
# copy your build output next to the manifest, then:
npkg-host pack --manifest manifest.json --payload-dir . --out example.npkg
npkg-host verify example.npkg
```

If you create projects with `dotnet new neutrino-*`, you do not need
these manifests — the SDK MSBuild targets generate the package for you
(see `docs/SDK-PACKAGING.md`). These templates are for hand-rolled
packages, scripts, and non-.NET tooling.
