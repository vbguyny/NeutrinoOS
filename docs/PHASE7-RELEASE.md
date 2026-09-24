# NeutrinoOS Phase 7 — Release Process

This document describes how to produce, verify and publish a NeutrinoOS
release. Everything runs in WSL2 Ubuntu 24.04 (build) plus PowerShell 7
on Windows 11 (appliance + installer).

## 1. Versioning

Semantic versioning `MAJOR.MINOR.PATCH`. v1.0.0 is the first release.
The version string appears in:

- the kernel banner (`[Boot] NeutrinoOS 1.0.0` / `/proc`-style info),
- `uname -a` output,
- `/etc/neutrinoos-release` (distro-style key/value file, written into
  release images by `build/p7-release-image.sh`),
- the release manifest (`dist/release.json`).

## 2. Reproducible builds

`make reproducible` (→ `build/reproduce.sh`) performs two clean builds
and compares `sha256sum` of `build/x64/BOOTX64.EFI` and
`build/x64/neutrinoos.img`. Requirements are wired into the Makefile:

- `SOURCE_DATE_EPOCH=315532800` is exported to the whole build; LLD
  stamps PE timestamps from it and GNU mtools (4.0.43) stamps FAT
  entries from it.
- `mformat -N 0x4E4F5301` pins the FAT volume serial number.
- `.NET` builds are deterministic for the pinned SDK (see
  `toolchain.lock`).

Verified: two consecutive clean builds produce byte-identical
`BOOTX64.EFI` and `neutrinoos.img` (recorded in
`docs/PHASE7-PERF-RESULTS.md`). If a future toolchain update breaks
determinism, `make reproducible` fails loudly.

`toolchain.lock` pins the exact toolchain versions used; regenerate it
with `bash build/gen-toolchain-lock.sh`.

## 3. Building the release artifacts

```bash
make reproducible          # prove determinism (optional but expected for releases)
make release
```

`make release` runs:

1. `build/p7-release-image.sh` — composes the release image
   (`neutrinoos-gui.img`: GUI-console kernel variant, utility suite in
   `::/bin`, `/etc/profile`, `/etc/neutrinoos-release`).
2. `build/p7-release.sh` — produces in `dist/`:
   - `neutrinoos-1.0.0.img` — raw FAT volume image, bootable directly
     by UEFI firmware / hypervisors, writable to USB (see
     `scripts/flash-usb.ps1`),
   - `neutrinoos-1.0.0.qcow2` — `qemu-img convert -O qcow2` of the raw
     image for `qemu-system-x86_64` + OVMF,
   - `SHA256SUMS` — checksums of both images,
   - `SHA256SUMS.asc` — detached ASCII-armor GPG signature (when a
     signing key is present; the script prints instructions otherwise),
   - `release.json` — machine-readable manifest (version, date,
     artifacts with sizes and SHA-256, install hints).

Optional VirtualBox appliance (Windows host, after the VM exists):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-ova.ps1
# -> dist\neutrinoos-1.0.0.ova, checksum appended to SHA256SUMS
```

## 4. Signing keys

One-time generation (WSL or any machine with GPG):

```bash
gpg --quick-gen-key "NeutrinoOS Release <release@neutrinoos.invalid>" ed25519 sign never
gpg --armor --export release@neutrinoos.invalid > dist/RELEASE-KEY.asc
```

Publish `RELEASE-KEY.asc` alongside the release; verify with:

```bash
gpg --import RELEASE-KEY.asc
gpg --verify SHA256SUMS.asc SHA256SUMS
sha256sum -c SHA256SUMS
```

The generation command is also printed by `build/p7-release.sh` when no
key is present.

## 5. Installing on Windows 11

- **One-command installer** (VirtualBox OVA):
  `scripts/install-neutrinoos.ps1` (Hyper-V variant ships in the same
  file set; see `docs/PHASE7-INSTALL-WINDOWS.md`).
- **USB stick**: `scripts/flash-usb.ps1 -Image dist\neutrinoos-1.0.0.img -Drive E:`
  (administrator, typed disk-number confirmation, USB-only safety gate).
- **QEMU on Windows**: launch with the bundled OVMF firmware and the
  `.qcow2`; the exact command line is inside `dist/release.json`
  (`install.qemu`).

## 6. Release checklist

1. `make reproducible` → OK (byte-identical artifacts).
2. Full test suite: `build/p7-bench.sh`, `build/p7-bootloop.sh`
   (0 halts), `tests/run-phase7-tests.ps1` → all pass.
3. `make release`; inspect `dist/`.
4. Sign `SHA256SUMS`; export the public key.
5. Smoke-boot the `.qcow2` and the `.ova` on a clean host profile.
6. Publish: images, `SHA256SUMS(.asc)`, `release.json`,
   `RELEASE-KEY.asc`, `RELEASE-NOTES-v1.0.0.md`.
