# Phase 8 — Ecosystem Infrastructure

What a package author and a package consumer get beyond the SDK:
signing keys, repositories, package templates, community process and a
package listing site.

---

## 1. Package signing keys

* **Official project key** — `keys/` (`private.key` + `public.key`,
  fingerprint
  `5da1ed4fca245a0a08db88a42b187583a8eabc640f8a7eb129111f0fc7e1967b`).
  The private key is intentionally published: this is the *development*
  project key used for reproducible builds and test images. Release keys
  are generated offline; devices pin fingerprints at `npkg repo add`
  time. Policy: `keys/README.md`.
* **Developer keys** — `npkg-host keygen` writes
  `~/.neutrinoos/private.key` + `public.key` (Ed25519, 64-hex form).
  Publish the public key + fingerprint with your repository; users pin it
  when adding the repo.

## 2. Repositories and mirroring

* Multiple repositories with **priority ordering** (guest `npkg`):
  * `npkg repo add <name> <url> [--priority <n>] [--fingerprint <hex64>]`
  * lower number = higher priority, default **50**; ties order by name;
  * `repos.json` records `priority` per entry; `npkg repo list` shows a
    `PRIO` column in resolution order;
  * search/install/upgrade resolve sources in priority order, so the
    preferred repository wins version ties (a broken mirror is skipped
    with a warning, it never blocks local installs).
  * Verified in the guest acceptance test: two repos over the same
    fixture (backup@70 added first, local@10 re-added second) resolve
    local-first (`repository priority order (local@10 before backup@70)`).
* **URL schemes:**
  * `http://` — served by `npkg-repo-server` / any static host;
  * `file:///path` and absolute `/path` — local repositories (image
    fixtures, disk copies);
  * `https://` — **deferred**: the device has no TLS client in Phase 8
    (the DDK TLS 1.3 stack is server-side only; `npkg repo add` rejects
    https URLs with a clear message). Integrity over `http://` is still
    protected end-to-end by Ed25519 package/index signatures plus the
    pinned fingerprint; confidentiality against a network attacker is
    not. Public hosting should terminate TLS at a reverse proxy and use
    a tunnel/static mirror for devices until the client side lands.
    Plan: implement a TLS 1.3 client (X25519 + AES-128-GCM + Ed25519
    CertificateVerify) reusing the existing key schedule in
    `src/ddk/Tls/Tls13.cs`, with trust anchors from `repo.pub`-style
    pinning or an on-device CA store.

## 3. Package templates

`templates/package-templates/` — five ready-to-adapt `manifest.json`
files (application, utility, driver, library, web-app) with per-type
README instructions for `npkg-host pack --manifest ...`. For projects
created from the `dotnet new` templates the packing is automatic (SDK
MSBuild targets), so these are for hand-rolled and non-.NET packages.

## 4. Community documentation

| Document | Covers |
|---|---|
| `CONTRIBUTING.md` | build/test loop, code conventions, PR process |
| `CODE_OF_CONDUCT.md` | Contributor Covenant 2.1 |
| `docs/COMMUNITY-GUIDE.md` | bug reports, feature requests, publishing packages, community repository submissions, security contact |
| `docs/PACKAGE-GUIDELINES.md` | naming (reverse-DNS, FAT 8.3), semver, licensing (SPDX + redistribution), signing/security requirements, submission checklist |

## 5. Website

`website/` — dependency-free static site: live package listing from a
repository index (`config.js` → `REPO_URL`, sample data otherwise),
getting-started steps, documentation links. Hosts anywhere static
(GitHub Pages, Netlify, any web server); see `website/README.md`.

## 6. Verification

| Check | Where | Result |
|---|---|---|
| Repo priority ordering + full npkg regression | `build/p8-npkg-test.sh` (guest, QEMU) | **26 PASS / 0 FAIL** incl. `repository priority order (local@10 before backup@70)` |
| Official key + package templates + docs + site | `build/p8-t5-ecosystem.sh` | see `t5 ecosystem summary` |
| SDK unaffected | `tests/run-phase8-tests.ps1 -Only sdk` | ALL-PASS |

Notes from bring-up: the guest runtime has no `System.Int32.Parse`
(the Tier-0 JIT cannot resolve it) — npkg parses numbers manually now;
`pkill` needs `-f` for qemu names (names > 15 chars match nothing).
