# Phase 10 — exFAT Filesystem

NeutrinoOS implements exFAT end to end: a from-scratch driver in the
DDK, kernel block-device plumbing that exposes every attached disk to
the utilities, user-space tooling (`mkexfat`, `fsck.exfat`,
`exfatlabel`, `exfatattrib`), shell/VFS integration (mount, umount,
df -T, USB auto-mount) and a package (`neutrinoos.utils.exfat`).

No third-party filesystem code is used; every byte written matches the
Microsoft exFAT specification and is cross-checked against
`mkfs.exfat`/`fsck.exfat` (exfatprogs) and the Linux `exfat-fuse`
driver (see `docs/PHASE10-INTEROP.md`).

## Volume Structure

The driver implements the full on-disk format:

- **Boot region** — Main Boot Sector, 8 Extended Boot Sectors and the
  OEM Parameters sheet (sectors 0–11) plus the identical Backup Boot
  Region (sectors 12–23). The 32-bit boot checksum (rotate-right-add
  over sectors 0–10, skipping bytes 106/107/112) is computed on write
  and verified on every mount; a bad Main region is restored from the
  Backup region during repair.
- **File Allocation Table** — 1 or 2 FATs (both are written and kept in
  sync when 2 are present), 32-bit entries, EOC 0xFFFFFFFF, free 0,
  `FAT[0] = 0xFFFFFFF8`, `FAT[1] = 0xFFFFFFFF`.
- **Cluster heap** — cluster sizes 4 KiB … 32 MiB (the formatter picks
  4 KiB until the heap exceeds 512 MiB, doubling from there, the same
  shape `mkfs.exfat` uses). Sectors are 512 B or 4096 B.
- **Allocation Bitmap** — one bit per cluster, metadata clusters marked
  during format, loaded into memory on mount and flushed with the
  VolumeDirty protocol.
- **Up-case Table** — the canonical 5836-byte compressed table
  (checksum `0xE619D30D`), generated into the DDK as
  `ExFatStdUpcase` by `build/gen-exfat-std-tables.py`; decompressed on
  mount and used for name hashing.
- **Root directory** — a FAT chain starting at `RootDirectoryCluster`
  (classically cluster 4).
- **Volume flags** — `VolumeDirty` set on a read-write mount and
  cleared on unmount/writeback; `PercentInUse` refreshed on unmount.
  Both live outside the boot checksum, exactly as the specification
  requires.

Directory entries implemented: Volume Label (0x83), Allocation Bitmap
(0x81), Up-case Table (0x82), File (0x85) + Stream Extension (0xC0) +
File Name (0xC1), with set checksums and name hashes validated on
read and recomputed on every write. File names are Unicode, up to 255
characters, case-insensitive via the up-case table.

## Driver Architecture

```
utilities (mkexfat, fsck.exfat, mount, ls, cat, ...)
   │  ProtonOS.DDK.Storage.ExFat (driver lives in the DDK)
   ├── ExFatVolume      boot/FAT/bitmap/upcase parsing + sector I/O
   ├── ExFatDirectory   entry-set read/write, growth, rename rebuilds
   ├── ExFatFileHandle  reads, growth (contiguous → chained), VDL
   ├── ExFatFileSystem  IFileSystem surface (mount, open, mkdir, ...)
   └── KernelBlockDevice ── Kernel_BlockDevice* exports ── kernel
        │                         BlockDeviceRegistry
        │                    (hda/hdb…, nvme0, sda…)
        ▼
   drivers: AHCI (JIT'd statics)   NVMe   USB mass storage (in-kernel)
```

The DDK assembly is loaded once and shared by the kernel world and
every utility, so a mount created by the `mount` utility is visible to
`ls`, `df` and the shell. The kernel JITs the driver statics it needs
(block I/O accessors, the `AutoMount` service, the VFS path bridge) and
calls them through function pointers, the same pattern the AHCI boot
file helpers use.

## System.IO / VFS bridge

`System.IO` in korlib reaches the kernel through the `FileBoot*` /
`DirBoot*` exports, which historically served every path from the boot
FAT volume. Phase 10 adds `VfsPathBridge` (DDK): any path under a
non-root VFS mount (`/mnt/usb/sda`, `/proc`, …) is routed to the
mounted filesystem first; everything else keeps the boot-volume
behaviour. That is what makes `cat /mnt/usb/sda/file.txt` and
`echo text > /mnt/test/new.txt` work for every utility and the shell
without per-tool changes.

## Write path guarantees

- **Allocation**: fresh files try a contiguous run first
  (`NoFatChain`), falling back to FAT chains when the heap cannot
  extend in place; chained streams extend by appending and re-linking.
- **Size accounting**: `ValidDataLength` bounds reads; the persisted
  `DataLength` is the *exact* file size (Phase 10 corrected an early
  build that stored the cluster-rounded allocation there, which made
  Linux see padded files). Allocation is reconstructed as
  `AlignUp(size)` on open.
- **Holes**: unallocated clusters inside a chained stream read as
  zeros (sparse files are preserved, not filled).
- **Crash consistency**: `VolumeDirty` is set while a volume is
  mounted read-write and cleared on a clean unmount; `fsck.exfat`
  reports and (with `-y`) clears it.

## Multi-disk boot safety

The kernel initially picked the *last* AHCI drive as the boot (FAT)
volume, which breaks as soon as a second disk is attached. The boot
file helpers now probe for the FAT-carrying drive (`GetBootFatDevice`,
cached); the ext2 root mount keeps its own selection rules.

## Limits

- Compressed up-case tables other than the canonical table are not
  supported (the standard table is always used for hashing).
- exFAT texFAT/redundant FAT is out of scope.
- File names are compared using the up-case table; NFD→NFC
  normalisation is not applied (same as mkfs.exfat-created volumes).
- `fsck.exfat` refuses nothing; it repairs boot regions, checksums,
  entry sets, chains, the bitmap and volume flags.
