# Phase 5 - Acceptance Guide (Windows 11, from scratch)

This document verifies every checklist item in `specs/phase-5.md`
(Terminal Shell and Utilities) on a fresh Windows 11 machine. Steps 1-3
come from `docs/BUILD-WINDOWS.md`; the later steps are Phase 5 specific.

## 0. Prerequisites

* Windows 11 with WSL2 (Ubuntu 24.04) and the .NET 10 SDK inside WSL
  (`export DOTNET_ROOT=/usr/share/dotnet`),
* QEMU (`qemu-system-x86_64`) and OVMF firmware in WSL
  (`apt install qemu-system-x86 ovmf mtools`),
* `bflat` (kernel AOT compiler) available as on Phase 1.

## 1. Build and boot (serial console)

```bash
cd ~/neutrino
./build.sh            # clean build (kernel + boot image, ~1-2 min)
./run.sh              # boot in QEMU, serial console attached (~35 s)
```

The boot ends in the shell prompt:

```
[SHELL] NeutrinoOS console ready.
Type 'help' for available commands.
neutrinoos> _
```

Phase 5 test builds can also use the helper scripts (local, gitignored):
`build/p5-all.sh` (rebuild + build 32 utilities + deploy run.img),
`build/p5-session.sh`, `build/p5-sysinfo-test.sh`,
`build/p5-tab-test.sh`, `build/p5-net-test.sh`.

## 2. Base utilities (checklist 1-2)

```
neutrinoos> pwd
/
neutrinoos> ls
APPS  ARGSAPP.DLL  BIN  DRIVERS  EFI  ETC  ...
neutrinoos> mkdir /test
neutrinoos> echo hello > /test/a.txt
neutrinoos> cat /test/a.txt
hello
neutrinoos> cp /test/a.txt /test/b.txt
neutrinoos> mv /test/b.txt /test/c.txt
neutrinoos> ls /test
A.TXT  C.TXT
neutrinoos> rm -r /test
neutrinoos> cd /apps ; pwd ; cd / ; pwd
/apps
/
```

`ls`, `cat`, `echo`, `mkdir`, `rm`, `cp`, `mv`, `cd`, `pwd` all run as
external .NET 10 utilities (except `cd`/`pwd`, which are built-ins).

## 3. Pipelines and redirection (checklist 3-6)

```
neutrinoos> ls /bin | wc -l
35                       (number of entries in /bin)
neutrinoos> echo hello > /test.txt
neutrinoos> cat /test.txt
hello
neutrinoos> echo world >> /test.txt
neutrinoos> cat /test.txt
hello
world
neutrinoos> wc -l < /test.txt
2
```

Automated: `build/p5-session.sh` asserts these exact outputs (pipes
verified with 3/1/hello world/16 markers in earlier runs).

## 4. Background execution (checklist 7)

```
neutrinoos> sleep 2 &
[jobs] [1] pid 1001 started: sleep 2
neutrinoos> jobs
  [1] pid 1001  running (or done)  sleep 2
```

`jobs` also shows the final state `done (exit 0)` after the sleep
completes. `ps` (utility) lists the same job table plus kernel threads,
and `kill <id>` cancels a queued job (`killed`, exit 130). Model and
limitations: `docs/PHASE5-SHELL.md` (cooperative, single-threaded).

## 5. System utilities (checklist 12-17)

```
neutrinoos> gc
[gc] collecting (mark-only) ...
[gc] heap: ... objects ... duration ... ms
neutrinoos> df
Filesystem        Size      Used     Avail  Use%  Mounted on
NEUTRINOOS       64511      2586     61925    4%  /boot (Ahci/Fat32)
neutrinoos> free
Mem:   total ... used ... free ... KB
GC heap: ... KB allocated (... objects, ... KB free)
neutrinoos> uname -a
NeutrinoOS 0.5 phase5 x86_64
neutrinoos> date
2026-09-23 03:15:30
neutrinoos> uptime
up 00:00:40 (40 seconds)
neutrinoos> env
PATH=/bin:/apps
HOME=/
...
neutrinoos> export FOO=bar ; env | grep FOO
FOO=bar
neutrinoos> unset FOO ; env | grep FOO ; echo $?
1
```

Automated: `build/p5-sysinfo-test.sh` runs 16 assertions over these
utilities (all PASS as of the final run).

## 6. History, tab completion, PS1 (checklist 18-20)

```
neutrinoos> history
  1  ls
  2  env
  ...
neutrinoos> ec<TAB>          (completes to "echo ")
neutrinoos> cat /hel<TAB>    (path completion: /HELLO...)
neutrinoos> l<TAB>
logout
ls                           (candidate list; type "s /bin" to run ls /bin)
neutrinoos> export PS1='\u@\h:\w\$ '
root@NeutrinoOS:/$
```

History persists across reboots (`/history.txt` is written on exit and
loaded at startup). Automated: `build/p5-tab-test.sh` (5 markers, all
PASS as of the final run).

## 7. Networking (checklist 8-11)

Boot with a NIC (QEMU user-mode networking):

```bash
# from build/p5-net-test.sh: starts local HTTP/TCP servers on the host,
# then boots QEMU with:
#   -netdev user,id=n0 -device virtio-net-pci,netdev=n0
```

```
neutrinoos> ifconfig
lo: flags=LOOPBACK  mtu 1500
    inet 127.0.0.1  netmask 255.0.0.0
eth0: flags=UP  mtu 1500
    inet 10.0.2.15  netmask 255.255.255.0
    gateway 10.0.2.2
    dns 10.0.2.3
    ether 52:54:00:12:34:56
neutrinoos> ifconfig eth0 down ; ifconfig eth0 up
eth0: interface down / eth0: interface up
neutrinoos> ping -c 1 127.0.0.1
PING 127.0.0.1: 40 data bytes
40 bytes from 127.0.0.1: icmp_seq=1 ttl=64 time=0.0 ms
--- 127.0.0.1 ping statistics ---
1 packets transmitted, 1 received, 0% packet loss
neutrinoos> dhcp
dhcp: requesting lease on eth0 ...
eth0: leased 10.0.2.15 netmask 255.255.255.0 gateway 10.0.2.2 dns 10.0.2.3
neutrinoos> ping -c 1 10.0.2.2          (the host side, real ICMP)
40 bytes from 10.0.2.2: icmp_seq=1 ttl=64 time=2 ms
neutrinoos> dns 10.0.2.2
10.0.2.2 -> 10.0.2.2
neutrinoos> netstat
INTERFACES ... TCP CONNECTIONS ... COUNTERS ...
neutrinoos> wget http://10.0.2.2:8099/hello.txt
hello from the host
wget: HTTP/1.1 200 OK, 20 bytes to stdout
neutrinoos> wget -O /dl.txt http://10.0.2.2:8099/hello.txt ; cat /dl.txt
hello from the host
neutrinoos> curl http://10.0.2.2:8099/hello.txt
hello from the host
neutrinoos> curl -d test=1 http://10.0.2.2:8099/submit
posted 6 bytes
neutrinoos> ssh -p 2222 10.0.2.2
ssh: TCP connection to 10.0.2.2:2222 established - server is reachable
ssh: the SSH protocol handshake is not implemented in Phase 5
neutrinoos> ssh 10.0.2.2
neutrinoos: ssh: could not connect to 10.0.2.2:22 (timeout or refused)
```

Without a NIC the same commands still work where local (127.0.0.1
ping, ifconfig's loopback line) and degrade with clear messages
otherwise.

Automated: `build/p5-net-test.sh` runs the full sequence against local
servers (`net-http-server.py`, `net-tcp-server.py`) and checks
15 assertions. The ssh limitation (TCP reachability only) is
documented in `docs/PHASE5-UTILITIES.md`.

Tested with the `skip-boot-tests` marker file: Phase 5 NIC sessions
skip the legacy in-kernel network self-tests (they JIT-saturate the
runtime); the utilities themselves provide the validation.

## 8. .NET 10 / no C or C++ (checklist 21-22)

* Every utility and DDK assembly builds as `net10.0`; the shell
  executes them through the kernel's Tier-0 JIT (`[run] /bin/*.dll`
  lines in the transcript).
* No C or C++ anywhere:

```
$ find . -name '*.c' -o -name '*.cpp' -o -name '*.cc' -o -name '*.h'
(no results)
```

All kernel, bootloader, driver, korlib, shell and utility sources are
C#; the only non-C# build inputs are the Phase 1 EFI stub and linker
scripts (assembly/linker artifacts, not C/C++).

## 9. Regression: earlier phase docs

* `docs/BUILD-WINDOWS.md` - clean Windows build still works.
* `docs/PHASE2-ACCEPTANCE.md`, `docs/PHASE3-ACCEPTANCE.md`,
  `docs/PHASE4-ACCEPTANCE.md` - their scripted checks are unaffected;
  `build/p5-session.sh` also re-exercises the Phase 4 application set
  (`/bin` covers both suites).

## 10. One-shot verification

From PowerShell (Windows):

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-phase5-tests.ps1
```

runs build + all four scripted sessions in WSL and prints a PASS/FAIL
summary. `scripts/phase5-demo.sh` (WSL) and `scripts/phase5-demo.ps1`
(Windows) run a scripted demo tour for a live audience.
