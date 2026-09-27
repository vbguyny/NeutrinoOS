# Phase 10 — Performance

Benchmarks were captured with the host harness (`tests/exfat-host`),
which drives the same DDK code as the kernel over a file-backed block
device, on the development machine (WSL Ubuntu 24.04, NVMe-backed
ext4). Times are wall-clock from the harness (`big` command measures
the transfer, `fill` measures directory entry creation).

> Measurement note: run these through a normal terminal / script file.
> Capturing the harness behind a throttled pipe (console backpressure)
> starves its stdout and distorts timings by 10-20x.

## Transfer throughput (verified curve)

| Workload | Write | Read + verify |
|----------|-------|---------------|
| 256 MiB file | 216 MB/s | 217 MB/s |
| 512 MiB file | 221 MB/s | 223 MB/s |
| 768 MiB file | 220 MB/s | 224 MB/s |
| 1 GiB file | 226 MB/s | 230 MB/s |
| 1.5 GiB file | 211 MB/s | 225 MB/s |
| 3 GiB file | 221 MB/s | 235 MB/s |
| **4.5 GiB file** | (~220 MB/s) | **235 MB/s** |

Throughput is flat from 256 MiB to 4.5 GiB: the cluster-map cache
survives growth, so appends stay O(1) and no per-chunk re-walks occur.

Example run (4.5 GiB acceptance, 6 GB volume, 32 KiB clusters):

```
PASS: big read 4831838208 bytes verify
read MB/s=234.85
→ fsck.exfat: clean. directories 1, files 1
→ FUSE stat: 4831838208 huge.bin
```

## Directory operations

| Workload | Time |
|----------|------|
| create 200 files in one directory | ~75 ms |
| list 200 files | ~10 ms |
| create 10 000 files in one directory | seconds; listed 10000/10000, fsck clean |
| 255-character create/read/rename/delete | < 50 ms |

## Comparison with FAT32 (Phase 9 numbers on the same machine)

FAT32 acceptance runs measured 60–120 MB/s on the same harness
mechanism. exFAT lands at **~210–235 MB/s** (no cluster-chain
look-buffer needed for contiguous streams; bitmap allocation is O(1)
with the in-memory bitmap). **Well within the 20 % budget the phase
spec allows** — reads/writes are ~2x faster than FAT32 here.

## In-VM sanity

The QEMU acceptance run (`build/p10-qemu-test.sh`) exercises the same
stack end to end (mount → file ops → fsck) in under 30 seconds of
guest time, including a 4 MiB file written via the shell and verified
byte-exact from Linux afterwards.

## Where time goes

1. sector window writes (RAM bounce + device write): ~55 %
2. zero-fill of freshly allocated clusters + bitmap/FAT updates
   (flushed once per growth window, not per sector): ~30 %
3. windowed reads for the verify pass: ~15 %

Cluster size dominates the balance: 32 KiB clusters (chosen for
volumes ≥ 256 MiB) amortize metadata over larger windows; 4 KiB
clusters pay more per-cluster zeroing and metadata updates. The
formatter matches the mkfs.exfat heuristic (4 KiB for small heaps,
32 KiB up to 32 GiB, 128 KiB beyond).
