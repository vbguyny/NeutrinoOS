# Phase 10 — exFAT Tooling

Four utilities ship with the system (`src/utilities/…`, packaged as
`neutrinoos.utils.exfat`):

| Tool | Purpose |
|------|---------|
| `mkexfat` | format a block device as exFAT (in-guest formatter) |
| `fsck.exfat` | check and repair an exFAT volume (`-y` repairs) |
| `exfatlabel` | read / set / clear the volume label |
| `exfatattrib` | list files with attributes; set +r/+h/+s/+a |

All four operate on **block devices from the kernel registry**
(`hda`/`hdb`/…, `nvme0`, `sda`/`sda1`/… for USB sticks), not on files.
That is the difference to the host-side test harness
(`tests/exfat-host`), which drives the same DDK code over image files.

## mkexfat

```
usage: mkexfat [-L label] [-c cluster] [-s sector] [-f fats] <device>
  -L label    volume label (max 11 characters)
  -c cluster  cluster size in bytes (4K..32M, suffixes K/M)
  -s sector   sector size: 512 or 4096 (default 512)
  -f fats     number of FATs: 1 or 2 (default 1)

mkexfat -L DATA /dev/hdb
```

Writes Main + Backup boot regions (checksummed), the FAT(s), a clean
allocation bitmap, the canonical up-case table and an empty root
directory, then reads the result back and verifies the boot checksums
and the up-case checksum before reporting success. The geometry
heuristic matches `mkfs.exfat` closely enough that images written by
either tool inter-operate in both directions (see
`docs/PHASE10-INTEROP.md`).

## fsck.exfat

```
usage: fsck.exfat [-y] <device>
  -y   repair automatically; without it the volume is only read

fsck.exfat /dev/hdb
fsck.exfat -y /dev/hdb
```

Checks performed:

1. Main/Backup boot regions (signature + checksum; repair copies the
   good region over the bad one).
2. Up-case table checksum (repair recomputes it).
3. `VolumeDirty` (reported; cleared under `-y`; `PercentInUse`
   rewritten).
4. Every directory entry set: checksum, name hash, name validity,
   `ValidDataLength <= DataLength`.
5. Cluster chains: double allocation, FAT loops, chains longer than
   the stream needs (trimmed), short chains (reported).
6. Allocation bitmap reconciliation: orphaned clusters are freed,
   missing allocations are claimed (repair).

Exit code 0 = clean or fully repaired; 1 = problems remain.

## exfatlabel

```
exfatlabel /dev/hdb              # print the label
exfatlabel /dev/hdb NEWNAME      # set (max 11 characters)
exfatlabel /dev/hdb ""           # clear
```

Creates the 0x83 label entry when missing. Works on volume level
(mounts the volume transiently; the device must not be mounted
elsewhere).

## exfatattrib

```
exfatattrib /dev/hdb                     # list: drhsa flags + name + size
exfatattrib /dev/hdb +rh important.txt   # add read-only + hidden
exfatattrib /dev/hdb -a archive.log      # clear archive
```

Specs combine `+`/`-` tokens (`+rh`, `-s`, `+a-h`). The read-only
attribute is enforced by the driver (writes fail with access denied);
hidden/system are stored only. Changes rewrite the directory entry set
and its checksum.

## mkexfat + fsck in the shell

```
neutrinoos> mkexfat -L DATA /dev/hdb
formatting hdb as exFAT label="DATA" ...
formatted hdb (exFAT, 49152 KB, verified)
neutrinoos> fsck.exfat /dev/hdb
fsck.exfat: checking hdb
fsck.exfat: 0 error(s), 0 repaired, 0 remaining
fsck.exfat: clean
```

## npkg package

The four tools are packaged as `neutrinoos.utils.exfat` (signed,
`packages/neutrinoos.utils.exfat/manifest.json`):

```
bash build/p10-package.sh       # hosts tools → /root/p10npkg/repo
bash build/p10-npkg-deploy.sh   # image with the repo, tools absent from /bin
bash build/p10-npkg-test.sh     # on-device: npkg install + use the tools
```

The device flow:

```
neutrinoos> npkg repo add local /repo
neutrinoos> npkg search exfat
neutrinoos> npkg install neutrinoos.utils.exfat
installed neutrinoos.utils.exfat 1.0.0
neutrinoos> mkexfat -L FROMNPKG /dev/hdb
neutrinoos> fsck.exfat /dev/hdb
```
