# Phase 10 — Acceptance Evidence

Every task in `specs/phase-10.md` with the command that demonstrates it
and the observed result. Reproduce end to end with:

```
powershell -ExecutionPolicy Bypass -File tests\run-phase10-tests.ps1
```

## Task 1 — Volume structure parser

| Requirement | Evidence |
|-------------|----------|
| Boot region + backup, checksums | `build/p10-interop.sh`: boot checksums byte-identical to mkfs.exfat for 5 variants; `build/p10-qemu-test.sh` mounts volumes written by our formatter |
| 512 B and 4096 B sectors | formatter `-s 4096` covered by host tests; 512 B everywhere in QEMU |
| 1 and 2 FATs | formatter `-f 2` (both FATs written); `fsck.exfat` refuses 2-FAT volumes, validated with exfat-fuse instead |
| 4 KiB–32 MiB clusters | interop variants 4K/32K/128K; formatter heuristic covers up to 32 MiB |
| Up-case table | canonical compressed table, checksum 0xE619D30D verified against mkfs.exfat images |
| >4 GiB volumes / files | `build/p10-big4g.sh`: 4.5 GiB file on a 6 GB volume written + read-verified byte-exact (PASS: big read 4831838208 bytes), fsck clean, Linux FUSE stat = 4831838208 bytes |

## Task 2 — Directory entry parser

| Requirement | Evidence |
|-------------|----------|
| Entry sets (0x85/0xC0/0xC1) + label/bitmap/upcase entries | host smoke: 200-file directory created + listed; long-name test |
| 255-character names | host smoke `longname` pass |
| Checksums + name hashes | `build/p10-corrupt-test.sh`: entry-set checksum corruption detected + repaired |
| >10 000-entry directories | `build/p10-big-tests.sh`: `fill /big 10000` → 10000/10000 listed, exfatprogs fsck clean (directories 2, files 10000); directory growth (windowed reads, End-marker capacity guard) exercised across the boot battery too |

## Task 3 — Driver (IFileSystem)

| Requirement | Evidence |
|-------------|----------|
| Mount/unmount, RO/RW | QEMU: `mount -t exfat` (rw) + `umount`; host `ro` mode tests |
| File/dir create, read, write, delete, rename | host smoke battery + QEMU guest transcript |
| Sparse + truncate + append semantics | host smoke (`SetLength`, append tests) |
| VolumeDirty protocol | corrupt test: dirty flag reported, cleared with `-y`; clean unmount clears it (fsck after QEMU run: clean) |

## Task 4 — VFS + kernel integration

| Requirement | Evidence |
|-------------|----------|
| Probe + mount options (ro/rw/uid/gid/umask/iocharset) | `mount -o ro|rw|uid=…|iocharset=` parsing unit-tested host-side; QEMU uses defaults + ro option |
| Mount points visible to all tools | QEMU: utility-mounted `/mnt/test` used by shell redirect + `ls`, `df -T` |
| USB automount at /mnt/usb/<device> | QEMU USB leg: `[automount] /dev/sda mounted at /mnt/usb/sda (exFAT, label "USBSTICK", …)` |
| df integration | QEMU `df -T` shows the exFAT row with label + sizes |

## Task 5 — Disk tooling

| Requirement | Evidence |
|-------------|----------|
| mkexfat | QEMU: `mkexfat -L MADE72 /dev/hdc` → exfatprogs (Linux) reads label MADE72, fsck clean |
| fsck.exfat (-y) | QEMU: in-guest `fsck.exfat /dev/hdb` → clean; corruption matrix 41/41 repair agreement with exfatprogs |
| exfatlabel | QEMU: `exfatlabel /dev/hdb` prints P10QEMU; hdc label verified from Linux |
| exfatattrib | corrupt suite: `+rh` set → fsck clean → `-r` clear |
| npkg package | `build/p10-npkg-test.sh` 8/8: tools absent → `npkg install neutrinoos.utils.exfat` → installed tools format + check a disk |

## Task 6 — Shell/utility integration

| Requirement | Evidence |
|-------------|----------|
| mount -t exfat / umount | QEMU transcript |
| df -T | QEMU transcript |
| Tab completion for /dev/ | `ShellCompletion` completes registry device names (`/dev/hd<TAB>`); covered by the completion unit path, not scripted in QEMU |
| Automount notifications | QEMU: `[automount] /dev/sda mounted…` printed; removal path unmounts (code path exercised by USB unbind tests in Phase 9 patterns) |

## Task 7 — Tests + docs

| Requirement | Evidence |
|-------------|----------|
| Test suite | `tests/run-phase10-tests.ps1` (6 legs) — host smoke, interop 90/90, corruption 41/41, QEMU 25/25, npkg 8/8, docs |
| Docs | `PHASE10-EXFAT.md`, `PHASE10-TOOLS.md`, `PHASE10-INTEROP.md`, `PHASE10-EXFAT-PERF.md`, `PHASE10-ACCEPTANCE.md`, `PHASE10-REPORT.md` |

## VirtualBox (real-hypervisor) acceptance

`powershell -ExecutionPolicy Bypass -File scripts\test-vbox-exfat.ps1` boots the
deployed image in VirtualBox 7.1 (EFI, IntelAhci SATA, 2 vCPUs) — a hypervisor
completely different from QEMU/KVM — with an exFAT data disk on SATA port 1 and
a blank disk on port 2, drives the guest over the emulated COM1 console, then
extracts the disks and reads them back from Linux (FUSE + exfatprogs):

- guest side (10 checks): shell boot, `mount`, `ls`, `cat`, redirect write,
  `mkdir`, `mv`, `df -T`, `umount`, in-guest `fsck.exfat` clean,
  `mkexfat` on the blank disk, `fsck.exfat` clean, `exfatlabel` readback
- host side (8 checks): exfat-fuse mounts the guest-written volume,
  guest-written file content, guest-created directory, rename, big.bin
  intact, fsck clean; blank data disk shows the in-guest `mkexfat` label
  and is fsck-clean

Result: **VBOXP10: ALL-PASS (18/18)**. Guest writes made through VirtualBox's
AHCI emulation are byte-visible to Linux tooling, and a volume formatted by our
in-guest `mkexfat` passes exfatprogs validation.

## Honest limitations

- No native Windows `chkdsk`/format cross-run in this environment
  (Linux exfatprogs + exfat-fuse stand in; the VirtualBox leg adds a third
  hypervisor/OS round-trip — guest writes on a Windows host are read back
  through WSL).
- Tab completion is verified by code path + manual interaction, not by
  the scripted QEMU legs.
- The 10 000-entry and 4.5 GiB cases run via `build/p10-big-tests.sh` and
  `build/p10-big4g.sh` (part of the runner's `big` leg).
- Run heavy host legs through a normal terminal; a throttled console
  pipe starves the harness and distorts timings (see the perf doc).
