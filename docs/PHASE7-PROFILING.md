# PHASE7-PROFILING.md — NeutrinoOS profiling infrastructure

Phase 7 Task 1. Everything below runs on the bare-metal guest (no
native dependencies); measurements are read back over the serial
console or through the virtual `/dev` files.

## 1. Boot timeline — `boottime`

`BootLog` records every boot stage with an HPET-nanosecond timestamp;
every `BootLog.Status` line during boot becomes a timeline entry.

```
neutrinoos> boottime
[boottime] NeutrinoOS boot timeline (ms since boot timer start)
  0 ms  Arch initialized (timers + interrupts)
  124 ms  Assemblies loaded
  183 ms  Kernel exports ready
  676 ms  DDK initialized
  682 ms  PCI enumerated
  4210 ms  Drivers bound
  36943 ms  Boot tests complete
  36946 ms  Scheduling enabled
  36950 ms  Console layer ready
  36958 ms  Boot complete
  36961 ms  Starting interactive shell
[boottime] last stage at 36961 ms
```

Reading (development image, boot tests enabled): the kernel reaches the
driver-bound state in ~4.2 s; the boot test suite accounts for 33 s of
the 37 s pre-shell time. The release/GUI image variant disables boot
tests, which is where the Phase 7 boot-time target is met (see
PHASE7-PERF-RESULTS.md).

## 2. JIT statistics — `jitstats`

`Tier0JIT.CompileMethod` wraps the compiler core and times every
top-level compilation with the HPET; `ProtonOS.Profiling.JitStats`
aggregates totals, min/max/average and the 16 slowest compilations
(assembly id + method token + native code size). `jitstats reset`
clears the counters (the running method count is re-synced).

```
neutrinoos> jitstats
[jitstats] methods compiled: 3093 (top-level: 93, failed: 0)
[jitstats] top-level wall time: 14333 ms total, 8921508 us max, 1579 us min, 154125 us avg
[jitstats] native code emitted (top-level): 110572 bytes
[jitstats] slowest compilations:
  8921508 us  asm=15 tok=0x06000027  code=8939B
  ...
```

The slowest entry (asm=15, ~8.9 s) is the first compile of the DDK
assembly — metadata resolution for a large assembly dominates the
first touch. This is the primary JIT optimization target.

## 3. GC statistics — `gcstats`

`GC.CollectMarkOnly` records its pause duration (HPET); `gcstats`
prints SOH/LOH usage from `GCHeap`, object counts, collection count and
pause times. The existing `gc` built-in triggers a diagnostic
mark-phase collection and now also reports the duration.

```
neutrinoos> gcstats
[gcstats] SOH: 2686 KB used (20333 objects), 385 KB free
[gcstats] LOH: 6054 KB (12 objects)
[gcstats] collections: 0   last pause: 0 ms   total pause: 0 ms
neutrinoos> gc
[gc] ... duration: 250 ms
```

## 4. Kernel sampling profiler — `perf` and `/dev/profiler`

The LAPIC timer interrupt handler (1 kHz) samples the interrupted RIP
into a 16 384-entry ring buffer while sampling is enabled. `perf`
start/stop/reset/dump controls it; the dump histograms the hottest
64-byte code ranges and resolves each to the nearest Tier-0 JIT method
(via a reverse lookup in `CompiledMethodRegistry`). AOT addresses are
printed raw and can be resolved offline with `tools/symlook.py`.

```
neutrinoos> perf start
[perf] sampling started (1 kHz); ...
neutrinoos> perf stop
[perf] sampling stopped; samples=5992
neutrinoos> perf dump
[perf] samples in ring: 5992   dropped: 0   state: stopped
[perf] hottest code ranges (64-byte buckets):
  5778 samples (96.4%)  0x0000000008001100  (aot/unknown - see tools/symlook.py)
  48 samples (0.8%)  0x00000000080DC980  (jit asm=0 tok=0x00000000F0001014)
  ...
```

The same report is served by the read-only virtual file
`/dev/profiler` (served by the kernel file bridge before the FAT
driver), so scripts can slurp it with `cat`.

## 5. Network counters — `netstat -s` and `/dev/netstats`

- `netstat -s` (DDK stack): per-protocol counters (TCP segments,
  ICMP, UDP), active connections and Phase 7 byte counters (IPv4 bytes
  in/out, TCP payload bytes in/out), sourced from the stack's RX
  (`ProcessIPv4`) and TX (`BuildIPv4Frame`) accounting points.
- `/dev/netstats` (kernel): interface-level frame counters taken at
  the NetworkBridge boundary — frames/bytes in and out, TX errors,
  uptime. Protocol-level TCP/UDP/ICMP counters live in `netstat -s`.

## 6. Benchmark suite — `tests/benchmarks/`

Four .NET 10 console applications built by
`build/p7-apps-build.sh` (installed into the image `/bin` by
`build/p7-bench.sh`):

| Benchmark | Measures | Method |
|-----------|----------|--------|
| `bench_alloc` | SOH + LOH allocation throughput (MB/s) | 20 000 × 256 B + 32 × 256 KB allocations, `Stopwatch` |
| `bench_file` | FAT32 write/read throughput (MB/s) | 1 MB `WriteAllBytes` + `ReadAllBytes`, verify |
| `bench_jit` | method-heavy workload; JIT throughput from `jitstats` deltas | 200 k iterations over distinct methods + string work |
| `bench_loopback` | kernel-loopback TCP throughput (MB/s) | `TcpServer` + `TcpSocket` to 127.0.0.1, 4 MB in 1400-byte chunks |

`System.Diagnostics.Stopwatch` in the JIT world is backed by the
kernel export `StopwatchBootNs` (nanosecond HPET clock) through the
token registry.

### Kernel loopback (new in Phase 7)

Frames addressed to a local address (the interface IP or 127.0.0.0/8)
now bypass ARP resolution and are delivered back through
`NetworkStack.ProcessFrame` by `NetworkPump.TransmitTxBuffer` instead
of the NIC — a real kernel loopback path used by `bench_loopback`.

## 7. How to run

```bash
# in WSL (after source changes)
bash build/p5-all.sh            # kernel + DDK + utilities + image
bash build/p7-apps-build.sh     # benchmark apps -> /root/phase7bin
bash build/p7-bench.sh          # boot + run the suite, print results
```

Measured numbers from the benchmark suite are recorded in
`docs/PHASE7-PERF-RESULTS.md`.
