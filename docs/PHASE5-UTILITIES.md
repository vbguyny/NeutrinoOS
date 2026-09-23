# Phase 5 - Utilities

NeutrinoOS Phase 5 ships 32 utilities under `src/utilities/<name>/Program.cs`.
They are ordinary .NET 10 console apps (no C/C++ anywhere) that are
built by `build/p5-apps-build.sh` into `/bin` of the boot image and run
in kernel mode through the Tier-0 JIT. All of them print `--help` and
return meaningful exit codes (0 success, 1 failure, 0 for
environmental "not available" cases, see each entry).

Common helpers live in `src/utilities/Common/UtilCommon.cs` (argument
parsing, whitespace splitting, glob matching, padding) and
`src/utilities/Common/HttpCommon.cs` (URL parsing, IP formatting, TCP
connect/pump helpers). Both are compiled into every utility.

## File system and text

| Utility | Usage | Notes |
|---------|-------|-------|
| `ls` | `ls [-l] [path...]` | multiple paths; `-l` long form (size, date if available) |
| `cat` | `cat [file...]` | `-` reads stdin (pipelines!) |
| `echo` | `echo [-n] args...` | `-n` suppresses the newline |
| `touch` | `touch file...` | creates empty files |
| `mkdir` | `mkdir [-p] dir...` | `-p` creates parents |
| `rm` | `rm [-r] [-f] path...` | recursive delete with `-r` |
| `cp` | `cp [-r] src dst` | recursive copy with `-r` |
| `mv` | `mv src dst` | rename/move |
| `head` | `head [-n N] [file]` | first N lines (default 10) |
| `tail` | `tail [-n N] [file]` | last N lines (default 10) |
| `wc` | `wc [-l] [-w] [-c] [file]` | lines/words/bytes; default all |
| `grep` | `grep [-i] [-v] [-n] pattern [file]` | literal substring match (no regex - documented limitation) |
| `find` | `find [path] [-name pattern]` | minimal recursive search (glob via `*`/`?`) |

## System information

| Utility | Usage | Source of the data |
|---------|-------|--------------------|
| `uname` | `uname [-a]` | kernel version string: `NeutrinoOS 0.5 phase5 x86_64` |
| `date` | `date` | CMOS RTC: `YYYY-MM-DD HH:MM:SS` |
| `uptime` | `uptime` | kernel tick counter: `up HH:MM:SS (N seconds)` |
| `free` | `free` | physical pages + GC heap (allocated, objects, LOH) |
| `ps` | `ps` | shell background jobs + kernel threads (id/state/stack) |
| `kill` | `kill [-9] pid` | cancels a queued shell job (cooperative, exit 130) |
| `df` | `df` | boot (FAT32) volume: label, total/used/free KB, use% |
| `mount` | `mount [device path]` | VFS mount table + boot volume info |
| `umount` | `umount path` | unmounts a VFS mount (e.g. `/proc`) |
| `env` | `env` | environment variables from the kernel table |

`df`/`mount`/`umount` details: the ext2 root mount cannot apply to the
FAT boot image, so the kernel skips the `/boot` VFS registration and
serves the boot volume through the AHCI driver's on-demand helpers;
`mount` therefore lists the real VFS table (e.g. `/proc` from procfs)
and reports the boot volume separately. `umount /proc` works and is
exercised by the tests.

## Networking

| Utility | Usage | Notes |
|---------|-------|-------|
| `ifconfig` | `ifconfig [iface [up\|down]]` | name/flags/MTU/inet/netmask/gateway/dns/ether |
| `dhcp` | `dhcp` | DISCOVER/OFFER/REQUEST/ACK on eth0, applies the lease |
| `ping` | `ping [-c N] host` | real ICMP echo via the NIC; `127.0.0.1` answered locally |
| `dns` | `dns host` | DNS resolution (IPv4 literals pass through) |
| `netstat` | `netstat` | interfaces, TCP connections (state/local/remote), counters |
| `wget` | `wget [-O file] url` | HTTP/1.1 GET (http:// only) |
| `curl` | `curl [-o file] [-d data] url` | GET or POST (`-d`), http:// only |
| `ssh` | `ssh [-p port] user@host` | TCP reachability check to the SSH port |

### How networking reaches the utilities

The virtio-net driver is a JIT-loaded assembly; utilities cannot call
driver code directly. At driver-bind time the kernel captures the
driver's `TransmitFrame`/`ReceiveFrame` entry points
(`src/kernel/Platform/NetworkBridge.cs`, the same technique the AHCI
file bridge uses) and registers them as `Kernel_Net*` exports. The DDK
gains `ProtonOS.DDK.Network.NetworkPump`, which pumps frames between
the NIC and the DDK `NetworkStack`. Because the DDK assembly (and its
static `NetworkManager`/`NetworkStack` state) is shared between the
driver, kernel and utilities, `ifconfig`/`netstat` read the live
interface and connection tables that the driver registered.

QEMU user-mode networking is the supported NIC setup
(`-netdev user,id=n0 -device virtio-net-pci,netdev=n0`); the guest gets
10.0.2.15 with gateway/DNS from slirp, and the host side is reachable
at 10.0.2.2 (used by the tests to run a local HTTP server).

### Documented networking limitations

* **ping 127.0.0.1**: answered locally by the ping utility (the DDK
  stack has no loopback netif in Phase 5). The request/reply are built
  and verified with the real DDK ICMP code so checksum/parse paths are
  exercised, but no NIC is involved. Other destinations are real.
* **DNS/DHCP** require the NIC; without it they print a clear message
  (`dhcp` exits 0 as an environmental condition, `dns` likewise).
* **ssh** implements the transport check only: it resolves the host,
  opens a real TCP connection to the SSH port and reports reachability;
  the SSH protocol handshake (wolfSSH) is out of Phase 5 scope
  (specs/phase-5.md allows documenting this). Exit 0 when reachable,
  1 otherwise.
* **wget/curl** support `http://` only (no TLS/HTTPS), decode bodies as
  ASCII (`?` for non-ASCII bytes) and read until the server closes the
  connection. On FAT images remember the 8.3 rule: no leading-dot
  output file names.
* The legacy in-kernel network self-tests (`VirtioNetEntry.TestPing`
  and friends) are gated by the `skip-boot-tests` marker file; Phase 5
  NIC sessions skip them because they JIT-saturate the runtime, and
  the utilities provide the validation instead.

## Shell-adjacent behavior

* Utilities run either directly (`ls /bin`) or inside pipelines
  (`ls /bin | wc -l`); `cat -`, `wc` and friends read the pipe or the
  `< file` redirection through the console/stdin API.
* `$PATH` lookup is `<name>.dll` in `/bin` or `/apps`; exit codes and
  stderr are propagated to the shell (`$?`).
* Without a NIC everything still works: network utilities degrade with
  a clear "network device not available" message.
