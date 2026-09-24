# PHASE7-PERF-RESULTS.md — measured performance (before/after)

All numbers from QEMU (q35, 2 GB RAM, 1 vCPU, virtio-net, 115200-baud
serial console) using the Phase 7 tooling (`boottime`, `jitstats`,
`gcstats`, `perf`, `tests/benchmarks/` via `build/p7-bench.sh`).

## Baseline (Phase 6 state → Phase 7 measurements)

| Area | Metric | Phase 6 baseline | Phase 7 measured | Change |
|------|--------|------------------|------------------|--------|
| Boot (dev image, boot tests on) | time to shell | ~41 s | 41 s (unchanged; release image skips boot tests) | — |
| Boot stage detail | drivers bound | — | 4.2 s | new visibility (`boottime`) |
| JIT | methods compiled at shell | 3093 | 3093 | — |
| JIT | top-level wall time / max | — | 14.3 s total / 8.9 s max (DDK first compile) | new visibility (`jitstats`) |
| GC | mark-phase pause | — | 250 ms (diagnostic collection) | new visibility (`gcstats`) |
| GC alloc | SOH throughput | — | 312 MB/s | benchmark added |
| GC alloc | LOH throughput | — | 273 MB/s | benchmark added |
| TCP loopback | throughput | not implemented | 0.36 MB/s (365 KB/s) | feature added (kernel loopback) |
| File I/O FAT32 | 1 MB write | — | 175 KB/s | benchmark added — **bottleneck** |
| File I/O FAT32 | 1 MB read | — | 1.16 MB/s | benchmark added |
| JIT workload | warm run | — | 31 ms; +5 methods, +3 top-level compiles (~20 ms wall) | `jitstats` deltas work |

Notes:

- Loopback before the `Quiet` switch measured ~85 KB/s; the per-packet
  trace lines at 115200 baud cost ~16 ms per segment. Suppressing
  protocol logging for bulk transfers improved throughput ~4x and is
  the pattern benchmarks use.
- The FAT write path (175 KB/s) performs per-byte/per-sector work in
  the JIT FAT driver; batching sector writes is the planned Phase 7
  optimization (target 2x, i.e. ≥350 KB/s).
- The loopback path processes one segment per `Send`/flush chain
  synchronously in a single CPU; batching multiple segments per flush
  is the planned optimization (target ≥1 MB/s).
- TLS/SSH handshake latency is measured externally against the live VM
  (see PHASE7-ACCEPTANCE.md); the release-image boot comparison
  (dev 41 s vs release image) is recorded in the release section.

## Optimization log

| # | Optimization | Before | After | Improvement |
|---|--------------|--------|-------|-------------|
| 1 | Protocol trace `Quiet` switch (serial console no longer throttles bulk transfer) | ~85 KB/s loopback | 365 KB/s loopback | ~4.3x |
| 2 | Window-aware send pacing in the loopback benchmark (no premature stop on full window) | 16.8 KB then stall | full 4 MB transferred | correctness + completion |
| 3 | Kernel loopback (new feature; Phase 6 had none) | n/a | 365 KB/s | new capability |
| 4 | Per-packet byte accounting (added for `netstat -s`) | n/a | negligible (<1%) | — |

## Targets (Phase 7 acceptance vs current)

| Target | Required | Current | Status |
|--------|----------|---------|--------|
| Boot time 30% faster than Phase 6 | ≥30% (release image) | release image skips 33 s of boot tests (visible via `boottime`); dev image unchanged at 41 s | pending final release-image measurement |
| Loopback TCP 2x Phase 6 | 2x | Phase 6 had no loopback; Phase 7 introduces it at 365 KB/s | capability added; batching optimization planned |
| TLS handshake 30% faster | ≥30% | not yet measured (baseline TBD) | pending |
| GC gen-0 pause 50% faster | ≥50% | pause instrumentation added; mark-only collection 250 ms | pending optimization wave |
| JIT compile 20% faster | ≥20% | 8.9 s first-DDK-compile outlier identified | pending optimization wave |
| File I/O 2x on FAT32 | 2x | write 175 KB/s (bottleneck), read 1.16 MB/s | pending optimization wave |

This document is updated as each optimization lands; the final column
values feed the Phase 7 report.
