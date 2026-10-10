# Running NeutrinoOS Under WHPX (Windows Hypervisor Platform)

QEMU on Windows can use WHPX hardware acceleration instead of TCG, but
NeutrinoOS was effectively unusable under it until the fixes below: boot
was ~8x slower than TCG and large transfers / long shell commands hung.

## Why WHPX made things worse

Every port I/O, MMIO access and HPET counter read is a **VM exit** under
WHPX (and comparable hypervisors such as NEM in VirtualBox), costing on
the order of microseconds each. TCG executes them as ordinary
interpreted instructions, so code tuned on TCG silently becomes
pathological under WHPX:

| Pattern | Cost under WHPX |
|---|---|
| ~1.9M framebuffer writes per boot (row-shift scroll) | ~6.3 s of boot |
| HPET read per uptime query | one VM exit per call |
| Iteration-count wait loops (`--timeout > 0`) | expire in microseconds at native speed, so timeouts fire constantly |

## What was changed

1. **TSC-based clock** — `HPET.FastUptimeNanoseconds()` derives uptime
   from `RDTSC` against the calibrated TSC base (`_tscBase`), so hot
   uptime queries never touch HPET MMIO. `Timer.GetUptimeMs()` in the
   DDK wraps this (kernel export `Kernel_GetUptimeMs`).
2. **Wall-clock deadline waits** — every virtio/AHCI wait loop was
   converted from "spin N iterations" to "spin until a
   `Timer.GetUptimeMs()` deadline" (1000 ms command / 2000 ms
   reset/link). Timeout paths no longer free descriptors or DMA buffers
   that the device still owns; buffers from timed-out transfers are
   reaped on a later completion (`ReapTimedOutBuffer`).
3. **VGA display-start scrolling** — `VgaTextDriver` keeps a 200-row
   ring in VRAM and scrolls by advancing the CRTC display-start
   register (2 port writes) instead of shifting ~4,000 framebuffer
   cells. The ring compacts back to its base once per ~150-175 scrolls,
   so the amortized cost is ~20 writes per scroll instead of ~8,000
   (each an MMIO VM exit under WHPX).

## Measured results (Windows QEMU 11.1, q35, 2G, 1 vCPU, `-vga std`)

Guest `[Boot]` timeline (the kernel's own uptime at "Boot complete"):

| Metric | TCG before | WHPX before | WHPX after |
|---|---:|---:|---:|
| Boot complete | 813 ms | 6640 ms | **571 ms** |
| scp 2 MiB up / down | 14.4 s / 14.0 s | 2.2 s / 2.0 s | 2.2 s / 2.0 s |
| ssh exec (uname) | 1.2 s | hung (pre-fix) | 238 ms |

AHCI boot path (QtEmu-style `ide-hd` on q35): **579 ms** guest boot
under WHPX. TCG boot also improved to 511 ms (scrolling is cheaper
everywhere).

## Reproducing the benchmark

`build/whpx-bench.ps1` boots the SSH-ready test image headless and
measures boot milestones plus scp/ssh timings:

```
powershell -File build\whpx-bench.ps1 -Accel whpx -Port 3322 -Label whpx
powershell -File build\whpx-bench.ps1 -Accel tcg  -Port 3323 -Label tcg
powershell -File build\whpx-bench.ps1 -Accel whpx -Port 3324 -Label ahci -Disk ahci
```

Requirements: Windows QEMU under `C:\Program Files\qemu`, and a
`build\whpx-test.img` prepared from the deploy image (user accounts +
authorized key via `build/p6-image-extras.sh`, `/etc/rc.local`
containing `sshd` for autostart, plus `skip-boot-tests` and
`console-active-vga` markers so the bench measures boot, not tests),
plus `build\p6key` matching `build\authorized_keys.pub`.

## Notes

- `-machine q35,pic=off,ioapic=kernel` from earlier guidance is not
  required; the fixes above remove the dependence on interrupt latency
  in the hot paths.
- MSI-X / interrupt-driven virtio remains a possible future
  optimization (it would cut idle polling cost), but is no longer
  needed for acceptable boot and transfer performance.
- The screendump check `build/wsl-vga-screendump.sh` boots headless
  with `-vga std`, drives the shell via QEMU monitor `sendkey`, forces
  200+ scrolls + a ring compaction (`seq 1 5000`) and converts PPM
  screendumps to PNG for visual verification.
