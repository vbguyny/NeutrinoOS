# Phase 8 — NeutrinoOS Package Manager (`npkg`)

`npkg` is the native NeutrinoOS package manager. It installs, removes and
upgrades `.npkg` packages (applications, utilities, drivers and libraries),
resolves dependencies, verifies Ed25519 signatures and SHA-256 checksums,
and maintains an atomic transaction journal.

Everything (CLI + package format + JSON + ZIP/inflate + Ed25519) is managed
C#: the guest-side `npkg` runs as a .NET 10 assembly on the Tier-0 JIT, the
host-side `npkg-host` runs on desktop .NET 10 (Windows 11 / WSL2).

---

## 1. Package format (`.npkg`)

A `.npkg` file is a ZIP archive with this exact layout:

```
manifest.json        package metadata (see §2)
checksums.sha256     SHA-256 of every payload file
signature.sig        Ed25519 signature (64 bytes) or empty when unsigned
payload/...          the package files (assemblies, data, config)
```

Format rules (implemented in `src/lib/NeutrinoOS.Packaging/NpkgPackage.cs`):

| Rule | Detail |
|---|---|
| `checksums.sha256` | one line per payload file, sorted by path: `<64-lowercase-hex><two spaces>payload/<path>`, `\n` terminated |
| `signature.sig` | Ed25519 signature over `manifest.json` bytes **concatenated with** `checksums.sha256` bytes, exactly as stored |
| signer fingerprint | `SHA-256(public key)` printed lowercase hex; recorded in `manifest.signer` |
| unsigned packages | `signature.sig` empty (installable only with `--allow-untrusted`) |
| compression | reader supports Stored (method 0) **and** Deflate (method 8, any standard ZIP tool); writer emits Stored |
| ZIP64 | not supported (archives ≥ 4 GiB are rejected with a clear error) |

## 2. `manifest.json`

| Field | Type | Purpose |
|---|---|---|
| `format` | string | always `"npkg/1"` |
| `name` | string | package name, e.g. `tests.hello-app` |
| `version` | string | semver, e.g. `1.2.3` or `1.2.3-rc.1` |
| `architecture` | string | `x86-64`, `arm64` or `any` |
| `author`, `description`, `license`, `homepage` | string | informational metadata |
| `signer` | string | SHA-256 fingerprint of the author public key (filled at pack time) |
| `installPath` | string | default install root (`/bin`, `/apps`, `/drivers`, `/lib`) |
| `dependencies` | object | package name → version constraint (see §4) |
| `provides` | array | capabilities: `application`, `utility`, `driver`, `library` |
| `entryPoints` | object | command name → payload-relative assembly, e.g. `{"helloapp":"helloapp.dll"}` |
| `scripts` | object | `pre-install`, `post-install`, `pre-remove`, `post-remove` shell command lines |
| `driver` | object | driver packages only: `class`, `vendorIds`, `deviceIds`, `entryPoint` |

Example (`tests/npkg/hello-app/manifest.json`):

```json
{
  "name": "tests.hello-app",
  "version": "1.0.0",
  "architecture": "any",
  "description": "Minimal NeutrinoOS application package (Phase 8 test fixture)",
  "installPath": "/apps",
  "provides": ["application"],
  "entryPoints": { "helloapp": "helloapp.dll" }
}
```

## 3. Repository format

A repository is a directory (local, `file://` or HTTP) containing:

```
repository.json        signed package index
repository.json.sig    Ed25519 signature over the raw index bytes
repo.pub               repository public key (raw 32 bytes)
*.npkg                 the packages the index lists
```

`repository.json`:

| Field | Purpose |
|---|---|
| `format` | `"npkg-repo/1"` |
| `name` | repository name |
| `revision` | index revision (informational) |
| `generated` | generation note (informational) |
| `packages[]` | one entry per package file: `name`, `version`, `architecture`, `description`, `filename`, `size`, `sha256`, `signer`, `dependencies`, `provides` |

Index generation and signing are host-side operations
(`npkg-host repo-index --dir <repo> --key <private.key>`); `npkg repo add`
fetches `repo.pub`, verifies the index signature and stores the key under
`/etc/npkg/trusted-keys/<repo>.pub`.

## 4. Dependencies and version constraints

Supported constraint syntax: `=1.2.3`, `>=1.0.0`, `>=1.0.0 <2.0.0`
(whitespace or comma separated = AND), `~1.2.3` (compatible: `>=1.2.3
<1.3.0`), `^1.2.3` (semver-compatible: `>=1.2.3 <2.0.0`, with the standard
`^0.x` and `^0.0.x` adjustments).

The resolver (`src/utilities/npkg/Resolver.cs`):

1. reads the installed database `/var/lib/npkg/installed.json`,
2. reads the repository indexes of the configured repositories,
3. walks dependencies transitively (topological, dependency-first order),
4. picks the highest candidate whose version matches the constraint
   (exact-architecture preferred over `any`),
5. reports conflicts: missing packages, two different versions of the same
   package in one transaction, or an installed version that no longer
   satisfies a constraint.

## 5. Commands

Guest (`npkg` on NeutrinoOS):

| Command | Purpose |
|---|---|
| `npkg install <name>[@<ver>] [--force] [--allow-untrusted]` | resolve, fetch, verify, install, update the database |
| `npkg remove <name> [--force]` | remove; refuses when an installed package depends on it |
| `npkg upgrade [<name>]` | upgrade one or all packages to the latest available version |
| `npkg list` | installed packages (name, version, arch, signer, description) |
| `npkg search <query>` | search all repository indexes |
| `npkg info <name>` | installed-first package details (dependencies, entry points, files) |
| `npkg repo add <name> <url> [--fingerprint <hex>]` / `repo list` / `repo remove <name>` | repository management (futures an index signature + trust pin) |
| `npkg verify <file.npkg>` | checksums + signature against trusted keys |

Host (`sdk/npkg`, runs on Windows 11 / WSL2): `keygen`, `fingerprint`,
`pack`, `sign`, `verify`, `repo-index`, `publish`.

## 6. Trust and signing

- Package trust is fingerprint-based: a package is accepted when
  `manifest.signer` matches a key under `/etc/npkg/trusted-keys/`
  (64-hex text or raw 32-byte file). Anything else is rejected unless
  `--allow-untrusted` is given (loud warning, `--force` recommended).
- `npkg repo add` prints the repository fingerprint; pass
  `--fingerprint <hex>` to pin it (mismatch aborts).
- Key generation: `npkg-host keygen` writes `private.key` (64-hex seed)
  and `public.key` (64-hex public key) to `~/.neutrinoos`.
- The Phase 8 test key in `tests/npkg/keys/` is **test-only**.

## 7. Filesystem layout on the device

| Path | Purpose |
|---|---|
| `/var/lib/npkg/installed.json` | installed package database |
| `/var/lib/npkg/journal.log` + `/var/lib/npkg/journal/` | transaction journal + file backups |
| `/var/lib/npkg/staging/` | extraction staging area |
| `/var/lib/npkg/cache/` | fetched packages |
| `/etc/npkg/repos.json` | configured repositories |
| `/etc/npkg/trusted-keys/` | trusted signer public keys |

Install placement:

- **utility** — entry assemblies → `<installPath>` (default `/bin`),
  wrapper `/bin/<entry>` (text: `run <assembly path>`),
- **application** — payload tree → `/apps/<name>/`,
  wrappers `/bin/<entry>` → `run /apps/<name>/<assembly>`,
- **driver** — payload tree + manifest copy → `/var/lib/npkg/drivers/<name>/`
  (consumed by the Phase 8 driver framework),
- **library** — payload tree → `/lib/`.

The kernel shell resolves the `/bin/<entry>` wrappers natively
(`ShellState.FindWrapper` + `TryParseWrapper`): a non-`.dll` command file in
`$PATH` whose first line is `run <path.dll> [args...]` is executed by the
Tier-0 JIT runner.

## 8. Atomicity and rollback

Every mutating command runs inside a journal transaction
(`/var/lib/npkg/journal.log`, one JSON record per line):

- before a file is created/overwritten/removed the journal records the
  operation; overwritten files are copied to `journal/<txn>/`,
- the database is rewritten (temp + copy, FAT has no atomic rename),
- on success the transaction is committed; on any failure the journal is
  replayed in reverse (delete added files, restore backups),
- `npkg` recovers an interrupted transaction automatically on the next run.

## 9. Build and test pipeline

```bash
# host tooling + test packages (signed local repo at /root/p8repo)
bash build/p8-sdk-build.sh           # builds sdk/npkg -> /root/p8sdk/npkg-host
bash build/p8-npkg-tests-build.sh    # builds fixtures, packs + signs the repo

# guest utility
bash build/p5-apps-build.sh          # builds all utilities incl. /bin npkg.dll

# device acceptance
bash build/p8-npkg-deploy.sh         # /root/npkgtest.img (utilities+repo+key)
bash build/p8-npkg-test.sh           # boots QEMU, drives npkg, asserts output
```

The acceptance run installs a utility, an application (runs it through the
`/bin` wrapper), a driver package and a three-deep dependency chain,
verifies dependency-guarded removal, upgrade behavior and signature
verification (`checksums: OK`).

## 10. Known limitations (Phase 8 scope)

- ZIP64 archives and Deflate **writing** are not supported (Stored is
  emitted; Deflate is read).
- `File.Move` on FAT is copy+delete; rollback therefore relies on journal
  backups rather than atomic renames.
- Directory operations are single-level (`CreateDirectory` needs parents to
  exist; `Delete` only removes empty directories).
- Package scripts execute through the kernel shell bridge (they run with
  system privileges; treat packages as trusted code).
- HTTP repositories are fetched in the clear; integrity comes from the
  signed index and signed packages. HTTPS repository fetching is host-side
  only in this phase.
