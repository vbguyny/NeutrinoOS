# SDK — CI/CD Integration

Templates live in `sdk/ci/`:

| File | For |
|---|---|
| `github-actions.yml` | GitHub Actions (copy to `.github/workflows/build.yml`) |
| `gitlab-ci.yml` | GitLab CI (copy to `.gitlab-ci.yml`) |

Both run the same pipeline:

1. checkout + .NET 10 SDK
2. fetch the NeutrinoOS SDK: clone the repository, build `npkg-host`,
   add it to `PATH`, `dotnet new install` the templates
3. `dotnet build -c Release` — the SDK targets produce the `.npkg`
4. `npkg-host verify` every package
5. optional: sign with an Ed25519 key from a secret
6. optional: assemble a repository directory (`npkg-host repo-index`)
   and upload packages + repository as build artifacts

---

## Signing in CI

Generate the key once on your workstation:

```bat
npkg-host keygen            :: %USERPROFILE%\.neutrinoos\private.key
```

* **GitHub:** repository secret `NPKG_SIGNING_KEY` = the 64-hex private
  key. The template's signing step is a no-op when the secret is absent.
* **GitLab:** masked CI variable `NPKG_SIGNING_KEY` (same content).

The signing steps write the key to a temp file, `npkg-host sign` each
package and `repo-index --key` the repository, so every artifact is
verifiable by fingerprint:

```text
neutrinoos> npkg verify MyApp.npkg            # signer: ab12cd34...
```

## Publishing the repository from CI

The GitHub workflow uploads two artifacts: `npkg-packages` and
`npkg-repository`. To serve them continuously, deploy the `repo/`
directory behind any static web server (or run `npkg-repo-server` on a
host) and point devices at it:

```text
neutrinoos> npkg repo add ci https://packages.example.com/repo
neutrinoos> npkg update
```

Hardening notes for a public repository:

* sign the index (`--key`) and keep `repo.pub` published next to it —
  devices verify `repository.json` against `repo.pub`;
* consider GitHub Pages / Netlify for the static index+packages;
* `npkg-repo-server` is HTTP-only (Phase 8); put a TLS reverse proxy in
  front for public hosting.

## Adapting the templates

* **Many projects in one repo:** run `dotnet build -c Release` at the
  solution level; the SDK targets run per project, producing one `.npkg`
  each. The `for p in bin/Release/net10.0/*.npkg` patterns in the
  templates are written for single-project repos — switch to a recursive
  glob (`**/*.npkg`) or an explicit list.
* **Drivers:** replace the verify step with the manifest pack flow
  (`npkg-host pack --manifest ... --payload-dir ...`) as in
  `docs/SDK-PACKAGING.md`.
* **No template install needed:** you can skip `dotnet new install` if
  the app was authored directly against the SDK props/targets
  (`build/NeutrinoOS.App.props|targets` from the archive).
