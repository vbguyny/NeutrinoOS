# Phase 10 — Interoperability Report

The exFAT implementation is validated against two independent
reference implementations on Linux:

- **exfatprogs 1.2.2** — `mkfs.exfat` (volume creation) and
  `fsck.exfat` (consistency checking).
- **exfat-fuse (FUSE 1.4.0)** — the Linux userspace driver, used to
  read and write volumes between NeutrinoOS runs (the WSL kernel has
  no native exFAT module).

All results below were produced on the development machine and are
reproducible with `tests/run-phase10-tests.ps1`.

## 1. Reading volumes created by mkfs.exfat (`build/p10-interop.sh`)

Five volume variants, each seeded with Linux-side files by mounting
via exfat-fuse:

| Variant | Geometry | Result |
|---------|----------|--------|
| default | 512 B sectors, 4 KiB clusters, 1 FAT | read ✔ / written-to ✔ / fsck clean ✔ |
| `-c 4K` | small clusters | ✔ / ✔ / ✔ |
| `-c 32K` | large clusters | ✔ / ✔ / ✔ |
| `-c 128K` | very large clusters | ✔ / ✔ / ✔ |
| `--pack-bitmap` | fully packed allocation bitmap | ✔ / ✔ / ✔ |

For every variant the harness (a) probed + mounted the volume through
the DDK, (b) verified boot and up-case checksums, (c) read the Linux
files byte-exactly, (d) created/renamed/deleted files, and (e) ran
`fsck.exfat` expecting "clean".

**Result: 90 PASS / 0 FAIL.**

## 2. Linux reading volumes created by NeutrinoOS

- `mkfs`-style volumes from our formatter are accepted by
  `fsck.exfat` (boot checksum, up-case checksum, bitmap, root
  directory all verified) — e.g. `exfat.img: clean. directories 1,
  files 0`.
- Files written in-guest (shell redirect through the VFS bridge,
  `mkdir`, `mv`, 4 MiB writes) are byte-exact when read back via
  exfat-fuse, and `fsck.exfat` stays clean afterwards.
- USB sticks: a stick formatted by our `mkexfat` in the guest is
  readable by `exfatlabel` (Linux) and `fsck.exfat` reports clean.

## 3. Corruption handling agreement (`build/p10-corrupt-test.sh`)

Corruptions are injected at byte level and the reference checker is
asked to confirm both detection and repair:

| Corruption | Ours detects | Ours repairs | exfatprogs agrees |
|------------|--------------|--------------|-------------------|
| Main boot checksum | ✔ | ✔ (from Backup) | ✔ clean after our repair |
| Backup boot checksum | ✔ | ✔ (recomputed) | ✔ |
| Up-case table checksum | ✔ | ✔ | ✔ |
| Entry-set checksum (destroyed) | ✔ | ✔ | ✔ |
| Bitmap: orphaned cluster | ✔ | ✔ (freed) | ✔ |
| Bitmap: missing allocation | ✔ | ✔ (claimed) | ✔ |
| VolumeDirty flag | ✔ (note) | ✔ (cleared with `-y`) | ✔ |

**Result: 41 PASS / 0 FAIL** (baseline clean, per-corruption detect /
repair / verdict / reference-checker agreement, label + attribute
round-trips).

## 4. Windows interop caveat

The specification-level interop story is covered by Linux exfatprogs
(the same reference implementation Microsoft-aligns its test suites
with). A native Windows `Format-Volume` / `chkdsk` cross-run was **not
executed in this environment** (no Windows exFAT tooling available in
the WSL-based test harness); honesty note recorded in
`docs/PHASE10-ACCEPTANCE.md`.

## 5. What exfatprogs cannot tell us

- `fsck.exfat` (exfatprogs) refuses two-FAT volumes; those are
  verified with the Linux exfat driver instead.
- exfat-fuse writes are used as the third-party writer; kernel exFAT
  (5.7+) is not present in WSL.
