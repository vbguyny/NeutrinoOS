# Community Guide

How to participate in the NeutrinoOS ecosystem.

---

## Reporting bugs, requesting features

Open a GitHub issue. Useful bug reports include:

* what you ran (commands, image/version, x64 or ARM64, QEMU/VirtualBox),
* what you expected, what happened,
* logs: `qemu.log` (WSL), `dist\serial.log` (Windows QEMU),
  `build\vbox-gui-serial.log` (VirtualBox), or acceptance script output,
* for kernel faults: the `SYNC EXCEPTION` / `!!! RAWV` dump.

Feature requests: describe the use case first; if it touches the kernel
ABI or package format, note that in the issue (those need design review).

## Contributing code

See `CONTRIBUTING.md`. Phase work follows the spec files in `specs/`;
phase acceptance suites live in `tests/run-phase<N>-tests.ps1`.

## Publishing packages

### 1. Build and sign

```bat
dotnet new neutrino-console -n MyApp
cd MyApp
dotnet build -c Release
npkg-host keygen                                 :: once; keep private.key safe
npkg-host sign bin\Release\net10.0\MyApp.npkg %USERPROFILE%\.neutrinoos\private.key
```

### 2. Host a repository

```powershell
copy bin\Release\net10.0\MyApp.npkg $env:USERPROFILE\.neutrinoos\repo\
powershell scripts\start-repo-server.ps1 -Key $env:USERPROFILE\.neutrinoos\private.key
```

Or publish the directory as a static site (any web server) after
`npkg-host repo-index --dir repo --key ... --name myrepo`.

### 3. Tell users how to install

Include the repository URL and your **public key fingerprint** in your
project README so users can pin it:

```text
neutrinoos> npkg repo add myrepo http://my-host:8080 --fingerprint <hex64>
neutrinoos> npkg update
neutrinoos> npkg install MyApp
```

### 4. Community repository

The community repository (`main`) is a curated index of packages that
follow `PACKAGE-GUIDELINES.md`. To submit:

1. Publish your signed package + repository index somewhere stable
   (GitHub Releases/Pages works well).
2. Open a PR adding your repository (name, URL, fingerprint) to
   `sdk/community-repositories.json` (created on first submission) or
   open an issue with the same details.
3. A maintainer reviews naming/licensing/security compliance, then adds
   your fingerprint to the official index.

Packages signed with the **official project key** are reserved for
project-shipped software (`keys/README.md`).

## Security issues

Do not open a public issue for vulnerabilities. Contact the maintainers
privately; see `docs/PHASE7-SECURITY.md` for the security model and the
audit surfaces. Package signing uses Ed25519
(`docs/PACKAGE-GUIDELINES.md` covers the requirements).

## Status and roadmap

`docs/` holds the phase designs and reports; `specs/phase-*.md` are the
working specs; `docs/PHASE<N>-REPORT.md` describe what shipped.
