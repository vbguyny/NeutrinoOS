# Package Guidelines

Requirements for packages intended for the community repository
(`docs/COMMUNITY-GUIDE.md`). The npkg tooling does not enforce most of
these — reviewers do.

---

## Naming

* Package names: **reverse-DNS style**, lowercase, dot-separated:
  `com.example.myapp`, `org.neutrinoos.hello`.
  Only `[a-z0-9.-]`, start with a letter.
* Command / entry-point names: lowercase, `[a-z0-9]` only, **<= 8
  characters** if the package installs onto the FAT boot volume
  (`/apps`, `/bin`).
* File names inside packages: same restriction (FAT 8.3 names), no
  spaces, no mixed case.
* Do not squat: you may not register a name that is not yours (e.g. a
  company/product you do not represent).

## Versioning

* Strict semantic versioning: `major.minor.patch[-prerelease][+build]`.
* Breaking changes to an app's public behavior or data files: major.
* Never reuse a version number for different content — devices cache and
  verify by version + SHA-256.

## Licensing

* Every package must declare `license` in its manifest (SPDX identifier,
  e.g. `MIT`, `Apache-2.0`, `GPL-3.0-only`).
* Include the license text or a link in the package payload (`/lib`
  shared libraries especially).
* The license must permit redistribution (no proprietary-only packages
  in the community index).

## Security

* **Sign every package** you publish; signatures are Ed25519 (keys are
  32-byte seeds, 64 hex characters). Publish your public key +
  fingerprint with your repository.
* Keep your signing key out of the package and out of CI logs; CI should
  pass it via a masked secret (`docs/SDK-CICD.md`).
* Declare only the capabilities you use (`provides`: application,
  utility, driver, library, web, ...).
* Utilities run with user privileges; do not require root for normal
  operation. Drivers run in kernel space: they must only use
  `IDriverServices` and must release resources in `Stop()` even when
  `Start()` failed part-way (`docs/PHASE8-DRIVER.md`).
* No network access without the user's action (a command they run or a
  service they start); no telemetry.
* Report vulnerable dependencies to the maintainers rather than shipping
  a fix silently (coordinated disclosure, see `COMMUNITY-GUIDE.md`).

## Repository index requirements

For the community repository, the generated `repository.json` must:

* be signed (`repository.json.sig`) with the publishing key;
* ship `repo.pub` next to it;
* use `filename` entries that exist byte-for-byte (regenerate the index
  after any package change — the repo server does this automatically).

## Checklist before submitting

- [ ] names/files satisfy the naming rules,
- [ ] semver version, no reuse,
- [ ] `license` declared (+ license text in payload),
- [ ] package signed; `npkg-host verify` passes with the published key,
- [ ] repository index + `repo.pub` published; fingerprint documented,
- [ ] `npkg install` tested on a clean image (`docs/SDK-TESTING.md`).
