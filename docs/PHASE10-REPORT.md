# Phase 10 — Final Report

## Deliverables

| # | Task | Status | Where |
|---|------|--------|-------|
| 1 | Volume structure parser | **DONE** | `src/ddk/Storage/ExFat/ExFatVolume.cs`, `ExFatStructures.cs`, `ExFatStdUpcase.cs` (generated) |
| 2 | Directory entry parser | **DONE** | `src/ddk/Storage/ExFat/ExFatDirectory.cs` |
| 3 | Driver (`IFileSystem`) | **DONE** | `src/ddk/Storage/ExFat/ExFatFileSystem.cs`, `ExFatFileHandle.cs`, `ExFatDirectoryHandle.cs` |
| 4 | VFS + kernel integration | **DONE** | `src/kernel/Storage/BlockDeviceRegistry.cs`, `src/kernel/Exports/DDK/StorageExports.cs`, `src/kernel/Platform/BlockDeviceBootstrap.cs`, `AutoMountBridge.cs`, `src/ddk/Storage/ExFat/AutoMount.cs`, `src/ddk/Storage/VfsPathBridge.cs`, AHCI/NVMe registry statics, USB hooks |
| 5 | Disk tooling + npkg | **DONE** | `src/utilities/{mkexfat,fsck.exfat,exfatlabel,exfatattrib}`, `src/ddk/Storage/ExFat/{ExFatFormatter,ExFatFsck,ExFatTools}.cs`, `packages/neutrinoos.utils.exfat`, `build/p10-package.sh` |
| 6 | Shell integration | **DONE** | `mount`/`umount`/`df -T` utilities, `ShellCompletion` `/dev/` completion, automount notifications |
| 7 | Tests + docs | **DONE** | `tests/run-phase10-tests.ps1`, `build/p10-*.sh`, `docs/PHASE10-*.md` |

## Test results (this machine, 2026)

| Leg | Result |
|-----|--------|
| Host smoke (`p10-host-smoke.sh`) | **SMOKE: ALL-PASS** |
| Host interop, 5 variants (`p10-interop.sh`) | **90 PASS / 0 FAIL** |
| Corruption matrix (`p10-corrupt-test.sh`) | **41 PASS / 0 FAIL** |
| QEMU in-VM + USB automount (`p10-qemu-test.sh`) | **25 PASS / 0 FAIL** |
| npkg package (`p10-package/deploy/test.sh`) | **8 PASS / 0 FAIL** |
| >10 000-entry directory (`p10-big-tests.sh`) | 10000/10000 listed, **fsck clean** |
| >4 GiB file (`p10-big4g.sh`) | 4608 MiB written + verified, **fsck clean** |

Final: **`tests/run-phase10-tests.ps1` reports ALL-PASS.**

## Bugs found + fixed during bring-up (all under test)

1. **Tier-0 JIT call-graph eagerness**: the host-only `GCHandle` scratch
   path failed in-kernel JIT — replaced with an abstract backend so the
   kernel never statically references BCL types korlib lacks.
2. **Boot-volume selection**: the AHCI boot-file helpers used
   "last drive", so a second disk hijacked command execution → FAT
   probing selector (`GetBootFatDevice`).
3. **System.IO ↔ VFS**: korlib file APIs only saw the boot FAT →
   `VfsPathBridge` routes non-root mounts first (this is what makes
   `cat /mnt/usb/sda/...` and shell redirects work).
4. **File size semantics**: persisted `DataLength` was the
   cluster-rounded allocation (Linux saw 4096 B for a 22 B file) →
   exact size in the set, allocation tracked internally.
5. **USB READ CAPACITY endianness** (latent Phase 9 bug): little-endian
   parse of big-endian SCSI data → 24 MiB stick reported 4.29 B
   sectors; fixed with `Read32Be`.
6. **AutoMount double-mount**: manual `fs.Mount` + `VFS.Mount` failed
   the second call → `VFS.Mount` only.
7. **fsck up-case check-mode verdict**: missing `Unrepaired++` made a
   corrupted volume report CLEAN in read-only mode.
8. Host harness `big` command int overflow (`4608*1024*1024` wrapped to
   512 MiB, and sizes ≥ 2 GiB produced a negative first chunk) → 64-bit
   chunked implementation with clamped casts.
9. Cluster-map cache reset on every growth → O(n²) chain re-walks on
   large files; growth only appends, so the cache now survives it.

## Performance (host harness, this machine)

- Sequential write: **~210–226 MB/s** (flat from 256 MiB to 4.5 GiB)
- Sequential read: **~217–235 MB/s**
- 200-file directory: create ~75 ms, list ~10 ms; 10 000-file directory
  listed 10000/10000 with fsck clean.

Meets the "within 20 % of FAT32" budget (FAT32 measured 60–120 MB/s on
the same harness); throughput is ~2x faster at every size tested.

## Interop honesty notes

- Linux exfatprogs + exfat-fuse are the reference implementations
  used; no native Windows cross-run was possible in this environment.
- exfatprogs refuses 2-FAT volumes; those were exercised with
  exfat-fuse instead.

## Artifacts

- `build/p10-qemu-test.sh` — 25-check QEMU acceptance (log
  `/root/p10qemu.log`, `ALL-PASS`).
- `build/p10-npkg-test.sh` — 8-check on-device package acceptance.
- `tests/run-phase10-tests.ps1` — the umbrella runner (host, interop,
  corrupt, qemu, npkg, docs).
- `build/tokdump/` — MethodDef token dumper used to decode Tier-0 JIT
  failures (kept as a debugging tool).
