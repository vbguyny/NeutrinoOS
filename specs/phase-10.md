# ROLE

You are a senior systems engineer specializing in filesystem driver
implementation, on-disk data structure parsing, Virtual File System (VFS)
integration, and disk tooling for custom operating systems. You are
assisting in Phase 10 of a custom operating system project.

# PROJECT CONTEXT

Project name: NeutrinoOS
Base project: ProtonOS (a managed OS written entirely in C# using
  bflat's zero-library mode, with a Tier-0 JIT compiler).

Phase 1 status: COMPLETE. Graphics/framebuffer/GOP removed. Serial
  console at COM1 (0x3F8, 115200 8N1).

Phase 2 status: COMPLETE. UART 16550 driver (`/dev/ttyS0`) with
  interrupt-driven RX/TX. Line discipline with canonical/raw modes,
  editing, Ctrl+C/D/U, 32-entry history. Console Abstraction Layer
  (CAL) with `IConsoleDevice` and `ConsoleMultiplexer`. `korlib`
  implements `System.Console`, `System.IO.TextWriter`/`TextReader`,
  `System.ConsoleColor`, `System.ConsoleKey`,
  `System.Text.Encoding.UTF8`, `System.Environment`.

Phase 3 status: COMPLETE. VGA text-mode driver (`/dev/vga0`,
  80x25/80x50, ANSI parser, CP437). PS/2 keyboard driver (IRQ1,
  scancode set 1, key repeat, modifier tracking). CAL routes output
  to both consoles and switches active input automatically.

Phase 4 status: COMPLETE. Tier-0 JIT validates .NET 10 assemblies
  (C# 14). `korlib` expanded with `System.IO`,
  `System.Collections.Generic`, `System.Linq`, `System.Threading`,
  `System.Threading.Tasks` (synchronous minimal), `System.Text`,
  `System`, `System.Net`, `System.Globalization`,
  `System.Diagnostics`. Cross-assembly loading with
  `AssemblyLoadContext`.

Phase 5 status: COMPLETE. Production shell with tokenizer, parser,
  pipes, redirection, background execution, sequential and
  conditional execution. Built-in commands and external utilities
  (`.dll`, .NET 10) including `ls`, `cat`, `echo`, `mkdir`, `rm`,
  `cp`, `mv`, `wc`, `grep`, `ps`, `kill`, `sleep`, `df`, `mount`,
  `umount`, `uname`, `date`, `uptime`, `free`, `env`, `ifconfig`,
  `dhcp`, `ping`, `dns`, `netstat`, `wget`, `curl`, `ssh` (client),
  `gc`, `history`, `export`, `unset`.

Phase 6 status: COMPLETE. Hardened TCP/IP stack with POSIX-like
  socket API. Managed C# cryptographic primitives. TLS 1.2/1.3.
  SSH-2.0 server (sshd). Minimal user database. .NET 10 web hosting
  via ported Kestrel (or fallback HTTP/1.1 server). `/dev/random`.
  Minimal packet filter.

Phase 7 status: COMPLETE. Profiling infrastructure, performance
  optimizations, security hardening (W^X, ASLR, stack canaries,
  guard pages, syscall filtering, rate limiting, audit logging,
  NIST/RFC crypto test vectors), release packaging (VirtualBox
  `.ova`, QEMU `.qcow2`, raw `.img`, reproducible builds,
  `release.json` with GPG signature, Windows 11 PowerShell
  installer).

Phase 8 status: COMPLETE. Native package manager (`npkg`) with
  `.npkg` format, repository index, dependency resolver, atomic
  transactions, Ed25519 signing. Device driver framework with
  `IDevice`, `IDriver`, `IDriverHost`, `IDeviceTree`, driver
  registry, stable driver ABI, PCI/PCIe and VirtIO bus
  enumerators, hot-plug support. ARM64 (AArch64) port booting
  under QEMU `virt`. Developer SDK with templates, MSBuild
  packaging targets, `npkg` CLI, local repository server.

Phase 9 status: COMPLETE. USB stack (xHCI, EHCI, hub, HID,
  mass storage, CDC-ACM) running in user-mode driver hosts.
  IPv6 support (dual-stack with IPv4, ICMPv6, NDP, DHCPv6,
  SLAAC). HTTP/2 and HTTP/3 (QUIC) support in the web server.
  ACPI power management (table parsing, AML interpreter,
  `poweroff`, `reboot`, `sleep`, CPU C-states and P-states).
  Physical NIC drivers (Intel e1000e, Realtek RTL8168/8111,
  Intel i225/i226), physical storage drivers (NVMe, AHCI),
  physical serial drivers (16550, PL011, USB CDC-ACM).
  Hardware compatibility list.

**Current filesystem status**: NeutrinoOS currently supports
  FAT32 (read/write) and EXT2 (read/write) through the VFS
  (inherited from ProtonOS). **exFAT is NOT yet supported.** This
  is a gap in the original requirements: the original design
  called for "Support for HD (exFAT)" but exFAT was deferred
  and never implemented in Phases 1–9.

Target of the overall project: A console-only, headless managed
  OS that runs .NET 10 console applications and utilities on bare
  metal, with TCP/IP networking, SSH, curl, and the ability to
  host .NET web apps. No GUI, no graphical framebuffer, no
  window manager. The primary deployment target is
  VirtualBox and QEMU (not physical hardware).

# ENVIRONMENT

- Host OS: Windows 11 (x64)
- IDE: Visual Studio Code (latest stable) with the Remote - WSL
  extension
- Shell: PowerShell 7 on the host; bash inside WSL2 Ubuntu 24.04
- Toolchain (already installed in WSL2 from Phase 1):
  - .NET SDK 10.0 (with C# 14)
  - bflat (configured to target .NET 10)
  - clang / ld.lld (LLVM 17+)
  - GNU make
  - Python 3.11+
  - qemu-system-x86_64 with OVMF firmware
  - git
  - `mkfs.exfat` and `fsck.exfat` (from the `exfatprogs` package)
    for creating and verifying test exFAT volumes from WSL2
- Phase 9 boot verification used:
  `make run-qemu-vga` (boots to NeutrinoOS shell on serial and
  VGA consoles). `tests/run-phase9-tests.ps1` verifies USB,
  IPv6, HTTP/2/3, ACPI, and physical hardware drivers.
- Windows 11 host has VirtualBox 7.x installed for manual
  verification.
- Phase 10 introduces exFAT. The design must not introduce new
  native dependencies; all code must remain managed C#.

# PHASE 10 GOAL — "exFAT FILESYSTEM DRIVER"

Implement a complete, production-quality exFAT filesystem driver
for NeutrinoOS, integrated with the existing VFS. Phase 10
delivers a read/write exFAT driver, mount/unmount support, disk
tooling (format and check), shell integration, and a
comprehensive test suite that verifies interoperability with
exFAT volumes created by Windows 11, Linux, and macOS.

Phase 10 is complete when a user can attach a virtual hard disk
formatted as exFAT to a NeutrinoOS VM in VirtualBox or QEMU,
mount it, create/read/write/delete/rename files and directories,
unmount it, and verify that the changes are visible when the same
disk is attached to a Windows 11 host.

Phase 10 does NOT include: exFAT support on the boot volume
(the boot volume remains FAT32 for UEFI compatibility), exFAT
journaling (exFAT has no journal), TexFAT (transaction-safe
exFAT), or exFAT on the ARM64/RISC-V ports (the driver is
architecture-independent and will work on all ports, but Phase 10
verification targets x86-64).

# DETAILED TASKS

## Task 1 — exFAT volume structure parser

Implement the exFAT on-disk structure parser in C#. Reference the
Microsoft exFAT specification (the official specification is
available at
`https://learn.microsoft.com/en-us/windows/win32/fileio/exfat-specification`)
for all field layouts and validation rules.

- **Volume layout**: exFAT volumes are organized into:
  - **Main Boot Region** (sectors 0–11): Main Boot Sector
    (sector 0), Main Extended Boot Sectors (sectors 1–8),
    Main OEM Parameters (sector 9), Main Reserved (sector 10),
    Main Boot Checksum (sector 11).
  - **Backup Boot Region** (sectors 12–23): identical structure
    to the Main Boot Region, used for recovery.
  - **FAT Region**: one or two File Allocation Tables,
    starting at `FatOffset`, each `FatLength` sectors long.
  - **Data Region**: the Cluster Heap, starting at
    `ClusterHeapOffset`. The Cluster Heap contains the
    Allocation Bitmap, the Up-case Table, and all file and
    directory data.
- **Boot sector fields**: Parse and validate:
  - `JumpBoot` (3 bytes, must be `EB 76 90`).
  - `FileSystemName` (8 bytes, must be `EXFAT   `).
  - `MustBeZero` (53 bytes, must be all zero).
  - `PartitionOffset` (8 bytes, sector offset of the
    partition).
  - `VolumeLength` (8 bytes, volume size in sectors).
  - `FatOffset` (4 bytes, sector offset of the first FAT).
  - `FatLength` (4 bytes, length of each FAT in sectors).
  - `ClusterHeapOffset` (4 bytes, sector offset of the
    Cluster Heap).
  - `ClusterCount` (4 bytes, number of clusters in the
    Cluster Heap).
  - `FirstClusterOfRootDirectory` (4 bytes, cluster index of
    the root directory).
  - `VolumeSerialNumber` (4 bytes).
  - `FileSystemRevision` (2 bytes, high byte major, low byte
    minor).
  - `VolumeFlags` (2 bytes, bit 0 = ActiveFat, bit 1 =
    VolumeDirty, bit 2 = MediaFailure, bit 3 = ClearToZero).
  - `BytesPerSectorShift` (1 byte, 9 for 512 bytes, 12 for
    4096 bytes).
  - `SectorsPerClusterShift` (1 byte, 0 for 1 sector, up to
    25 for 32 MB clusters).
  - `NumberOfFats` (1 byte, 1 or 2).
  - `DriveSelect` (1 byte).
  - `PercentInUse` (1 byte, 0–100 or 0xFF if not available).
  - `BootCode` (390 bytes).
  - `BootSignature` (2 bytes, must be `55 AA`).
- **Boot checksum**: The Main Boot Checksum sector (sector 11)
  and Backup Boot Checksum sector (sector 23) contain a
  32-bit checksum repeated 128 times. Compute and validate the
  checksum over sectors 0–10 (for the Main Boot Region) and
  sectors 12–22 (for the Backup Boot Region). The checksum
  algorithm is defined in the exFAT specification (Section
  3.4). Reject the volume if the checksum does not match.
- **FAT**: The exFAT FAT is an array of 32-bit cluster
  indices. The first FAT entry (index 0) is `0xFFFFFFF8`,
  the second (index 1) is `0xFFFFFFFF`. For a file whose
  `NoFatChain` flag is clear, the FAT chains clusters
  together. For a file whose `NoFatChain` flag is set, the
  file's clusters are contiguous and the FAT is not used.
- **Allocation Bitmap**: The Allocation Bitmap is a critical
  primary directory entry in the root directory whose data
  is a bitmap of cluster allocation (1 bit per cluster, 1 =
  allocated, 0 = free). It is stored in the Cluster Heap.
- **Up-case Table**: The Up-case Table is a critical primary
  directory entry in the root directory whose data maps
  Unicode lowercase characters to uppercase for
  case-insensitive name comparison. exFAT uses a compressed
  representation: the first 128 entries are the ASCII
  uppercase mapping, followed by runs of the form
  `0xFFFF` (marker) + `count` (2 bytes) + `mapped_value`
  (2 bytes). Implement the full decompression.
- **Cluster heap**: The first cluster index is 2. Cluster
  `n` (for `n >= 2`) is located at sector
  `ClusterHeapOffset + (n - 2) * 2^SectorsPerClusterShift`.
  Sectors are `2^BytesPerSectorShift` bytes.

## Task 2 — exFAT directory entry parser

Implement the exFAT directory entry parser.

- **Directory entry set**: In exFAT, a file or directory is
  represented by a *set* of directory entries, all of which
  share the same `File` or `NoFatChain` bit pattern in their
  `EntryType` byte. The set consists of:
  - **File Directory Entry** (type `0x85`): contains the
    `SecondaryCount`, `SetChecksum`, `FileAttributes`,
    `CreateTimestamp`, `LastModifiedTimestamp`,
    `LastAccessedTimestamp`, `Create10msIncrement`,
    `LastModified10msIncrement`, `CreateUtcOffset`,
    `LastModifiedUtcOffset`, `LastAccessedUtcOffset`.
    The `SetChecksum` is a 16-bit checksum over all entries
    in the set.
  - **Stream Extension Directory Entry** (type `0xC0`):
    contains `GeneralSecondaryFlags` (bit 0 = AllocationPossible,
    bit 1 = NoFatChain), `NameLength` (1 byte),
    `NameHash` (2 bytes), `ValidDataLength` (8 bytes),
    `FirstCluster` (4 bytes), `DataLength` (8 bytes).
  - **File Name Directory Entry** (type `0xC1`): contains
    15 UTF-16 characters of the file name. A file name of
    up to 255 characters uses up to 17 File Name entries.
- **Entry types**: Implement the full set of entry types:
  - `0x81`: Allocation Bitmap (critical primary).
  - `0x82`: Up-case Table (critical primary).
  - `0x83`: Volume Label (critical primary).
  - `0x85`: File (critical primary).
  - `0x00`: End of Directory.
  - `0x01`–`0x7F`: Unused (skip).
  - `0xA0`: Volume GUID (benign primary).
  - `0xA1`: TexFAT Padding (benign primary).
  - `0xC0`: Stream Extension (critical secondary).
  - `0xC1`: File Name (critical secondary).
  - `0xC2`: Windows CE ACL (benign secondary).
  - `0xC3`–`0xFF`: Vendor Extension (benign secondary).
- **Checksum**: The `SetChecksum` in the File Directory Entry
  is a 16-bit checksum over all 32-byte entries in the set,
  skipping the `SetChecksum` field itself. The algorithm is
  defined in the exFAT specification (Section 6.3.4). Reject
  any entry set whose checksum does not match.
- **Name hash**: The `NameHash` in the Stream Extension
  Directory Entry is a 16-bit hash of the uppercase file name,
  used for fast name lookup. The algorithm is defined in the
  exFAT specification (Section 7.2.5). Compute and validate
  the hash.
- **Case-insensitive comparison**: Use the Up-case Table to
  perform case-insensitive name comparison, matching the
  behavior of Windows, Linux, and macOS exFAT drivers.

## Task 3 — exFAT driver implementation

Implement the exFAT driver in C#, integrated with the VFS.

- **Driver structure**: The exFAT driver implements the VFS
  filesystem interface (the same interface used by the FAT32
  and EXT2 drivers from earlier phases). The driver is a
  class (e.g., `ExFatFileSystem`) that implements:
  - `Mount(blockDevice, mountOptions)` → `IFileSystem`.
  - `Unmount()`.
  - `Open(path, mode)` → `IFileHandle`.
  - `Create(path, attributes)`.
  - `Delete(path)`.
  - `Rename(oldPath, newPath)`.
  - `ListDirectory(path)` → `IEnumerable<DirectoryEntry>`.
  - `GetFileInfo(path)` → `FileInfo`.
  - `SetFileInfo(path, FileInfo)`.
  - `Flush()`.
- **Read path**:
  - Resolve a path by walking directory entry sets from the
    root directory. For each component, read the directory's
    cluster chain, scan for a matching entry set, and
    descend.
  - Read file data by following the cluster chain (or
    contiguous clusters if `NoFatChain` is set).
  - Cache the Allocation Bitmap and Up-case Table in memory
    (they are loaded once at mount time).
- **Write path**:
  - Allocate clusters by scanning the Allocation Bitmap for
    free bits, setting them, and updating the FAT (if
    `NoFatChain` is clear).
  - Extend files by allocating additional clusters.
  - Write directory entry sets for new files and
    directories, computing the `SetChecksum` and
    `NameHash`.
  - Update the `ValidDataLength` and `DataLength` fields
    in the Stream Extension Directory Entry.
  - Update timestamps (create, last modified, last accessed)
    in UTC.
  - Update the `VolumeDirty` flag in the boot sector when
    the volume is mounted read/write, and clear it on
    clean unmount.
- **Directory operations**:
  - Create directories by allocating a cluster for the
    directory and writing an empty directory (with an
    End of Directory entry).
  - Delete directories (must be empty).
  - Rename files and directories by updating the File Name
    entries and recomputing the `SetChecksum` and
    `NameHash`. If the new name is longer, allocate
    additional File Name entries; if shorter, mark the
    surplus entries as unused (`0x00`).
- **File operations**:
  - Support files up to 2^64 - 1 bytes (the exFAT limit).
  - Support the `NoFatChain` optimization for contiguous
    files.
  - Support sparse files (clusters with `0` in the FAT,
    representing holes) for files with `NoFatChain` clear.
- **Error handling**:
  - Validate all on-disk structures before using them.
  - Return clear error codes (e.g.,
    `FileSystemCorrupt`, `FileNotFound`,
    `AccessDenied`, `DiskFull`).
  - Log errors to the console with the cluster index and
    sector offset of the corrupt structure.
- **Performance**:
  - Cache the Allocation Bitmap and Up-case Table in memory.
  - Cache recently accessed directory clusters.
  - Use the FAT to avoid scanning the Allocation Bitmap for
    cluster allocation when possible.
  - Document the performance characteristics in
    `docs/PHASE10-EXFAT-PERF.md`.

## Task 4 — VFS integration and mount support

Integrate the exFAT driver with the VFS.

- **Mount detection**: When a block device is mounted, the
  VFS probes for known filesystems. Add exFAT to the probe
  list:
  - Read sector 0.
  - Check for `JumpBoot == EB 76 90` and
    `FileSystemName == EXFAT   `.
  - Validate the boot checksum.
  - If valid, mount as exFAT.
- **Mount options**: Support:
  - `ro` (read-only).
  - `rw` (read-write, default).
  - `uid=<n>` (owner user ID for all files; default 0).
  - `gid=<n>` (group ID for all files; default 0).
  - `umask=<n>` (permission mask; default `022`).
  - `iocharset=utf8|ascii` (name encoding; default
    `utf8`).
- **Mount points**: Support mounting an exFAT volume at
  arbitrary mount points (e.g., `/mnt/usb`, `/mnt/data`).
- **Unmount**: Implement `umount` that flushes all dirty
  data, clears the `VolumeDirty` flag, and releases the
  block device.
- **Automount**: Support automounting USB mass storage
  devices (from Phase 9) when they contain an exFAT
  volume. The automount daemon detects the device, probes
  the filesystem, mounts it read-write at
  `/mnt/usb/<device>`, and notifies the shell.
- **`df` integration**: The `df` command must report exFAT
  volumes with total size, used space, free space, and
  mount point, using the Allocation Bitmap to compute
  usage.

## Task 5 — Disk tooling

Implement exFAT disk tooling for NeutrinoOS.

- **`mkexfat`**: A tool to format a block device as exFAT.
  - Options: `-L <label>` (volume label), `-c <cluster-size>`
    (cluster size in bytes: 4096, 8192, 16384, 32768, 65536,
    131072, 262144, 524288, 1048576, 2097152, 4194304,
    8388608, 16777216, 33554432), `-s <sector-size>`
    (512 or 4096), `-f <fats>` (1 or 2, default 1),
    `-r <revision>` (filesystem revision, default 1.0).
  - Write the Main Boot Region, Backup Boot Region, FAT,
    Allocation Bitmap, Up-case Table, and root directory.
  - Compute and write the boot checksum and the entry set
    checksums.
  - Verify the formatted volume with a self-check.
- **`fsck.exfat`**: A tool to check and repair an exFAT
  volume.
  - Verify the boot sector fields and the boot checksum.
  - Verify the FAT entries.
  - Verify the Allocation Bitmap against the FAT and the
    directory tree.
  - Verify the Up-case Table.
  - Verify all directory entry sets (checksums, name
    hashes, cluster chains).
  - Verify file sizes against cluster chains.
  - Report orphaned clusters, cross-linked clusters, and
    directory entry set errors.
  - Repair mode (`-y`) automatically fixes correctable
    errors.
- **`exfatlabel`**: A tool to read and set the volume label.
- **`exfatattrib`**: A tool to read and set file attributes
  (read-only, hidden, system, archive).
- **Integration with `npkg`**: Package the tooling as a
  `.npkg` utility package (`neutrinoos.utils.exfat`) so it
  can be installed via `npkg install`.

## Task 6 — Shell integration

Integrate exFAT with the shell.

- **`mount -t exfat <device> <mountpoint>`**: Mount an
  exFAT volume.
- **`umount <mountpoint>`**: Unmount an exFAT volume.
- **`mkexfat <device>`**: Format a block device as exFAT.
- **`fsck.exfat <device>`**: Check an exFAT volume.
- **`df -T`**: Show filesystem type in `df` output.
- **Tab completion**: Complete exFAT device paths and mount
  points.
- **Automount notifications**: When a USB mass storage
  device is plugged in and contains an exFAT volume, the
  shell prints a notification (e.g.,
  `[automount] /dev/sda1 mounted at /mnt/usb/sda1 (exFAT)`).

## Task 7 — Testing and documentation

- **Test suite**:
  - `tests/run-phase10-tests.ps1` (PowerShell for
    Windows 11) that:
    - Creates an exFAT virtual hard disk (`.vhd` or
      `.img`) on Windows 11 using `Format-Volume` or
      the `mkfs.exfat` tool from WSL2.
    - Attaches the disk to a NeutrinoOS VM in QEMU or
      VirtualBox.
    - Boots NeutrinoOS, mounts the exFAT volume, and
      verifies that the volume label, root directory
      contents, and file contents match what was
      written on Windows 11.
    - Creates, writes, reads, renames, and deletes
      files and directories on NeutrinoOS.
    - Unmounts the volume, shuts down NeutrinoOS,
      detaches the disk, and re-attaches it to
      Windows 11.
    - Verifies that the changes made on NeutrinoOS
      are visible on Windows 11.
- **Interoperability tests**:
  - Create exFAT volumes with:
    - Windows 11 `Format-Volume` (512-byte and 4096-byte
      sectors, 1 FAT and 2 FATs, various cluster sizes).
    - Linux `mkfs.exfat` from `exfatprogs`.
    - macOS `newfs_exfat` (if available; document if not
      tested).
  - Mount each volume on NeutrinoOS and verify
    read/write.
- **Edge case tests**:
  - Files at the 255-character name limit.
  - Files at the 16 EB size limit (simulated with
    sparse files).
  - Directories with many entries (spanning multiple
    clusters).
  - Files with `NoFatChain` set (contiguous) and clear
    (fragmented).
  - Volumes with 1 FAT and 2 FATs.
  - Volumes with 512-byte and 4096-byte sectors.
  - Volumes with 4 KB to 32 MB clusters.
  - Corrupt boot sector (verify rejection).
  - Corrupt FAT (verify `fsck.exfat` detects and
    repairs).
  - Corrupt Allocation Bitmap (verify `fsck.exfat`
    detects and repairs).
  - Corrupt directory entry set (verify rejection).
  - Corrupt Up-case Table (verify rejection).
- **Performance benchmarks**:
  - Sequential read throughput (MB/s).
  - Sequential write throughput (MB/s).
  - Random read/write IOPS.
  - Directory listing time for a directory with 10,000
    entries.
  - File creation time for 1,000 files.
  - Compare to FAT32 and EXT2 on the same block
    device.
- **Documentation**:
  - `docs/PHASE10-EXFAT.md` — exFAT driver architecture,
    volume structure, directory entry parsing, read/write
    paths, error handling.
  - `docs/PHASE10-EXFAT-PERF.md` — performance
    characteristics and benchmark results.
  - `docs/PHASE10-TOOLS.md` — `mkexfat`, `fsck.exfat`,
    `exfatlabel`, `exfatattrib` usage.
  - `docs/PHASE10-INTEROP.md` — interoperability test
    results with Windows 11, Linux, and macOS.
  - `docs/PHASE10-ACCEPTANCE.md` — step-by-step
    verification for every acceptance criterion below
    from a fresh Windows 11 machine.
  - `PHASE10-REPORT.md` — summary of changes, blockers,
    deviations.

# CONSTRAINTS

- All code must be C# (plus the existing assembly
  intrinsics). Do NOT add C or C++ files to the kernel,
  bootloader, drivers, `korlib`, shell, utilities, VFS, or
  exFAT driver.
- Do NOT introduce a graphical framebuffer, GUI, mouse
  support, or a window manager. This phase is strictly
  console-only.
- Do NOT depend on OpenSSL, libssh, libcurl, libusb,
  libmsquic, or any other native library. The exFAT driver
  is self-contained managed C#.
- Do NOT implement exFAT journaling (exFAT has no
  journal), TexFAT (transaction-safe exFAT), or exFAT on
  the boot volume. The boot volume remains FAT32 for UEFI
  compatibility.
- Do NOT implement exFAT on the ARM64 or RISC-V ports in
  Phase 10. The driver is architecture-independent and
  will work on all ports, but Phase 10 verification
  targets x86-64.
- Do NOT use the term "TTY" as a project name or suffix.
  It is fine to use the Unix term "tty" in device paths
  and documentation.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to
  ProtonOS.
- Do NOT scope-creep into Phase 11+ (journaling, TexFAT,
  boot-from-exFAT, exFAT on ARM64/RISC-V verification).
- Every public type and method added to the VFS, the
  exFAT driver, or the tooling must have XML doc comments
  describing its Phase 10 semantics.
- All user-visible strings must say "NeutrinoOS".
- Reference the official Microsoft exFAT specification
  (`https://learn.microsoft.com/en-us/windows/win32/fileio/exfat-specification`)
  for all on-disk structure layouts, field definitions,
  checksum algorithms, and validation rules. Do not
  guess at field layouts.
- Reference the `picrap/exfat` C# project
  (`https://github.com/picrap/exfat`) for design
  inspiration. It is a C# exFAT accessor library that
  works at three levels (partition, entry, path) and is
  MIT-licensed. Do NOT copy its code; use it as a design
  reference only. The NeutrinoOS driver must be written
  from scratch against the exFAT specification.
- Reference the `relan/exfat` project
  (`https://github.com/relan/exfat`) for the FUSE-based
  exFAT driver design. It is GPL-2.0-licensed. Do NOT
  copy its code; use it as a design reference only.

# DELIVERABLES

1. An exFAT volume structure parser (boot sector, boot
   checksum, FAT, Allocation Bitmap, Up-case Table,
   Cluster Heap).
2. An exFAT directory entry parser (entry sets, File
   Directory Entry, Stream Extension Directory Entry,
   File Name Directory Entry, entry type dispatch,
   `SetChecksum`, `NameHash`, case-insensitive comparison
   via the Up-case Table).
3. An exFAT driver (`ExFatFileSystem`) implementing the
   VFS interface with mount, unmount, open, create,
   delete, rename, list, get/set file info, and flush.
4. VFS integration: filesystem probing, mount options,
   mount points, unmount, automount for USB mass storage,
   `df` integration.
5. Disk tooling: `mkexfat`, `fsck.exfat`, `exfatlabel`,
   `exfatattrib`, packaged as
   `neutrinoos.utils.exfat`.
6. Shell integration: `mount -t exfat`, `umount`,
   `mkexfat`, `fsck.exfat`, `df -T`, tab completion,
   automount notifications.
7. Test suite: `tests/run-phase10-tests.ps1`,
   interoperability tests, edge case tests, performance
   benchmarks.
8. Documentation: `docs/PHASE10-EXFAT.md`,
   `docs/PHASE10-EXFAT-PERF.md`, `docs/PHASE10-TOOLS.md`,
   `docs/PHASE10-INTEROP.md`,
   `docs/PHASE10-ACCEPTANCE.md`, `PHASE10-REPORT.md`.

# ACCEPTANCE CRITERIA

Phase 10 is complete when ALL of the following are true:

- [ ] `make image` and `make run-qemu-vga` still boot to a
      NeutrinoOS banner on both consoles, with no
      regressions from Phase 9.
- [ ] An exFAT volume created on Windows 11 (via
      `Format-Volume -FileSystem exFAT`) can be attached
      to a NeutrinoOS VM in VirtualBox or QEMU, mounted
      at `/mnt/test`, and its volume label, root
      directory contents, and file contents match what
      was written on Windows 11.
- [ ] `ls /mnt/test` lists the files and directories
      correctly, including names with Unicode
      characters, spaces, and mixed case.
- [ ] `cat /mnt/test/hello.txt` prints the correct file
      contents.
- [ ] `echo "hello from NeutrinoOS" > /mnt/test/new.txt`
      creates a new file, and the file is visible on
      Windows 11 after unmounting and re-attaching the
      disk.
- [ ] `mkdir /mnt/test/newdir` creates a new directory,
      and it is visible on Windows 11.
- [ ] `mv /mnt/test/new.txt /mnt/test/renamed.txt`
      renames a file, and the rename is visible on
      Windows 11.
- [ ] `rm /mnt/test/renamed.txt` deletes a file, and
      the deletion is visible on Windows 11.
- [ ] `mkexfat /dev/sdb` formats a block device as
      exFAT, and the resulting volume is mountable on
      Windows 11.
- [ ] `fsck.exfat /dev/sdb` checks an exFAT volume and
      reports no errors on a clean volume.
- [ ] `fsck.exfat -y /dev/sdb` detects and repairs a
      deliberately corrupted volume (corrupt FAT,
      corrupt Allocation Bitmap, corrupt directory
      entry set).
- [ ] An exFAT volume created on Linux (`mkfs.exfat`)
      can be mounted and read/written on NeutrinoOS.
- [ ] An exFAT volume created on macOS (`newfs_exfat`)
      can be mounted and read/written on NeutrinoOS
      (document if macOS testing is not available).
- [ ] Volumes with 512-byte and 4096-byte sectors can
      be mounted and read/written.
- [ ] Volumes with 1 FAT and 2 FATs can be mounted
      and read/written.
- [ ] Volumes with 4 KB, 32 KB, and 32 MB clusters can
      be mounted and read/written.
- [ ] Files with names at the 255-character limit can
      be created, read, renamed, and deleted.
- [ ] Files larger than 4 GB can be created, read, and
      written (tested with a 5 GB sparse file).
- [ ] Directories with more than 10,000 entries can be
      listed correctly.
- [ ] A USB mass storage device (from Phase 9)
      containing an exFAT volume is automatically
      mounted at `/mnt/usb/<device>`.
- [ ] `df -T` shows the exFAT volume with type `exfat`,
      total size, used space, free space, and mount
      point.
- [ ] The `VolumeDirty` flag is set when the volume is
      mounted read/write and cleared on clean unmount.
- [ ] `fsck.exfat` on a volume whose `VolumeDirty` flag
      is set reports the volume as dirty and recommends
      a check.
- [ ] Performance benchmarks show exFAT sequential
      read/write throughput within 20% of FAT32 on the
      same block device.
- [ ] No C or C++ files exist in the kernel, bootloader,
      driver, `korlib`, shell, utility, VFS, or exFAT
      driver directories.
- [ ] `docs/BUILD-WINDOWS.md` (from Phase 1) and
      `docs/PHASE2-ACCEPTANCE.md` through
      `docs/PHASE9-ACCEPTANCE.md` still work, and
      `docs/PHASE10-ACCEPTANCE.md` provides step-by-step
      verification for every checklist item above from a
      fresh Windows 11 machine.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps mapped to
   the seven tasks above.
2. **Repository layout** — the target directory tree after
   Phase 10, highlighting new and modified files.
3. **Code changes** — for each file to be created,
   modified, or deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created files)
     OR a unified diff (for modifications) OR a precise
     description (for deletions).
   - For large files (e.g., the exFAT driver, the
     `fsck.exfat` tool), provide the complete source; do
     not abbreviate with "..." unless the omitted region
     is boilerplate that is explicitly described.
4. **exFAT on-disk structure table** — a table listing
   each on-disk structure (Main Boot Sector, Backup Boot
   Sector, FAT, Allocation Bitmap, Up-case Table, root
   directory, File Directory Entry, Stream Extension
   Directory Entry, File Name Directory Entry), its
   location, and its purpose.
5. **Directory entry type table** — a table listing each
   entry type (`0x81`, `0x82`, `0x83`, `0x85`, `0xC0`,
   `0xC1`, `0xC2`, `0xA0`, `0xA1`), its name, and its
   role in a directory entry set.
6. **VFS interface table** — a table listing each VFS
   method, its exFAT implementation, and its error
   codes.
7. **Tool table** — a table listing each tool
   (`mkexfat`, `fsck.exfat`, `exfatlabel`,
   `exfatattrib`), its syntax, and its options.
8. **Build and test commands** — exact WSL2 bash
   commands and PowerShell commands for Windows 11 to
   build, run, and verify Phase 10 (QEMU and
   VirtualBox, with exFAT disks created on Windows 11
   and Linux).
9. **Acceptance checklist** — reproduce the checklist
   above, with a one-line note for each item explaining
   how it is satisfied.
10. **Deferred to later phases** — anything that came up
    that belongs to Phase 11+ (journaling, TexFAT,
    boot-from-exFAT, exFAT on ARM64/RISC-V
    verification, exFAT encryption).
11. **Open questions / assumptions** — anything
    ambiguous about the Phase 9 output, the existing VFS
    interface, the existing FAT32/EXT2 drivers, or the
    block device API that you assumed, and how the user
    can verify or correct them.

If any part of the Phase 9 output is unclear, or if the
existing VFS interface, FAT32 driver, or block device
API does not match your assumptions, state your
assumptions explicitly and proceed with a reasonable
layout consistent with a bflat-based managed kernel,
noting where the user must adjust paths.

Do not skip ahead to Phase 11+. Scope discipline is
mandatory: Phase 10 only.