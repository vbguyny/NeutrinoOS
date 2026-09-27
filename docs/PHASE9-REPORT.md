# NeutrinoOS Phase 9 — Final Report

"USB, IPv6, HTTP/3, ACPI POWER, HARDWARE ENABLEMENT"

Status: **complete** — all six tasks delivered and machine-verified
where a machine is available. Per-area documents carry the details;
`docs/PHASE9-ACCEPTANCE.md` carries the step-by-step verification and
`tests/run-phase9-tests.ps1` runs the whole set.

| Area | Doc | Commit |
|------|-----|--------|
| Task 1 — USB (xHCI, HID, BOT, CDC-ACM, hot-plug) | `docs/PHASE9-USB.md` | `a9eb043` |
| Task 2 — IPv6 dual-stack | `docs/PHASE9-IPV6.md` | `c109323` |
| Task 4 — ACPI power (S5/S3, AML, cpupower) | `docs/PHASE9-ACPI.md` | `183c7f0`, `681bcb8` |
| Task 3 — HTTP/2 + HTTP/3 | `docs/PHASE9-HTTP2-3.md` | `117b06a`, `d719280` |
| Task 5 — hardware (NVMe etc.) | `docs/PHASE9-HARDWARE.md`, `HARDWARE-COMPATIBILITY.md` | `6a157a6` |
| Task 6 — test suite + acceptance/report | this file, `PHASE9-ACCEPTANCE.md`, `tests/run-phase9-tests.ps1` | — |

## 1. USB (Task 1)

Delivered: polled xHCI stack (64-TRB command ring, 256-TRB event ring,
per-endpoint transfer rings), device enumeration, HID keyboard/mouse
decoding wired into the console layer, USB mass storage (BOT/SCSI,
READ CAPACITY/READ), CDC-ACM serial (`ttyUSB0…` ports, polled RX
ring), and PORTSC-mask hot-plug driven from the shell idle pump.

Verification: `build/p9-usb-test.sh` — **11 PASS / 0 FAIL**. The
keyboard check is end-to-end: `sendkey` types `version` through the
USB HID path and the shell runs it.

Deferred (documented in `PHASE9-USB.md`): EHCI/UHCI/OHCI, downstream
hub management, isochronous transfers, MSI for the event ring, real
`/dev/sda` VFS block nodes + FAT32/EXT2 mount on USB media, mouse
cursor routing.

## 2. IPv6 (Task 2)

Delivered: IPv6 header parse/build + checksums, ICMPv6 (echo,
neighbor solicitation/advertisement), NDP cache, SLAAC (RS/RA, prefix
+ router + DNS), DHCPv6 (stateful attempt + stateless
INFORMATION-REQUEST), DNS AAAA path with dual-stack fallback, and the
`ping6` / `dns6` / `netstat` utilities.

Verification: `build/p9-ipv6-acc3.sh` — **PASS=8 FAIL=0, zero JIT
faults**: SLAAC address + router learned, DHCPv6 REPLY processed,
`ping6 ::1` 2/2, `ping6 fe80::2` 3/3 (NDP + echo), `dns6` dual-stack
path, AAAA query observed on the wire.

Deviation: the in-guest `curl` utility is IPv4-only, so the spec's
`curl -6 http://[::1]:5000/` fetch is deferred to the IPv6 socket
work; ICMPv6/NDP/DHCPv6/SLAAC/DNS layers are verified.

## 3. HTTP/2 + HTTP/3 (Task 3)

Delivered: HPACK (static+dynamic tables, prefix-tree Huffman decode),
an HTTP/2 connection engine (framing, flow control, stream states,
`h2` ALPN + h2c Upgrade(101) + prior knowledge), QUIC v1 transport
(Initial/Handshake/1-RTT keys, packet protection, ACK/loss handling,
streams, QPACK-parity control streams), TLS 1.3-over-QUIC handshake
with ALPN `h3`, and QPACK (static-table codec via generated tables).

Verification:

- in-guest `h2test`: **12/12 checks** + summary PASS;
- host wire matrix over a TAP link (`build/p9-h2-tap.sh`):
  HTTP/1.1, h2 prior-knowledge, h2c upgrade, two requests on one h2
  connection, TLS `h2` ALPN, TLS HTTP/1.1 — all `rc=0`,
  `HTTP/2 200`, zero raw faults;
- HTTP/3 with a real QUIC client (aioquic): handshake complete ALPN
  `h3`, `GET /health` → `200`, `GET /` → `200` — `RESULT: PASS`.

Methodology notes worth keeping: slirp `hostfwd` never delivered
connections in this environment (ARP fine, no SYN), so host-side tests
use a **TAP link** (`10.0.2.2` host / `10.0.2.15` guest); the QUIC
bring-up was cracked with a raw aioquic relay + offline decryption of
the client's secrets, which produced the precise protocol diffs
(STREAM-frame LEN bit, QPACK `T` bit, SH extension length, transcript
append, Initial padding, RX key selection). `docs/PHASE9-HTTP2-3.md`
carries the full JIT-hazard and protocol-bug catalogue.

Deviation: the WSL curl lacks HTTP/3 support, so `curl --http3` is
replaced by the aioquic client; ports are 80/443 (spec text says
5000/5001).

## 4. ACPI power (Task 4)

Delivered: ACPI table parsing (FADT/DSDT/SSDT/MADT/MCFG/HPET), an AML
interpreter with the synthetic-method boot self-test, S5 `poweroff`
(PM1_CNT + `\_S5` SLP_TYP), `reboot` (reset register + warm-boot BSS
fix), S3 `sleep` entry, `cpupower` (C-state/P-state report, MWAIT
idle), and the CPU-idle integration.

Verification: `build/p9-acpi-test.sh` — **18 PASS / 0 FAIL
(ALL-PASS)**: `poweroff` shuts QEMU down by itself (no
`-no-shutdown`), `reboot` reaches a second shell prompt in the same
QEMU process, S3 entry reported with `\_S3` values, monitor shows the
VM suspended, `system_wakeup` flips it back to running, `cpupower`
reports C/P-states. VirtualBox `poweroff`/`reboot` verified live via
`scripts/test-vbox-power.ps1` (typed over the emulated keyboard; VM
state → `poweroff`; second boot banner after `reboot`).

Deviations: guest continuation after S3 wake is firmware-dependent on
QEMU (documented); QEMU q35 firmware declares no `_PTS`/`_WAK`.

## 5. Hardware enablement (Task 5)

Delivered: a full NVMe 1.x host controller driver JIT-compiled into
the kernel: reset (CC.EN / CSTS.RDY), admin + I/O queue pairs,
IDENTIFY controller/namespace, READ/WRITE/FLUSH with single-page PRPs,
DSTRD doorbells, polled completion with phase-bit tracking, and a
boot-time self-test. NIC documentation for the e1000e/RTL8168/i225
families with honest per-family statuses. HCL + physical test
procedure.

Verification: `build/p9-nvme-test.sh` — **NVME: PASS**:
`[NVMe] BAR0 at 000000C000004000 size ... (mapped)`, `model=QEMU NVMe
Ctrl`, `serial=NEUTRINO01`, LBA0 signature OK, write/flush/read/verify
PASS on the last LBA.

Three real bring-up pitfalls (now documented in
`docs/PHASE9-HARDWARE.md`):

1. **64-bit BARs above 4 GiB are outside the kernel physmap** (the
   map covers the first 4 GiB). QEMU places the NVMe BAR at
   `0xC000004000`; the driver maps it with `Memory.MapMMIO` (2 MiB
   pages, cache-disabled) — the same pattern the virtio driver uses.
2. **AQA/ASQ/ACQ must be programmed before `CC.EN` 0→1** — QEMU
   latches the queues at the enable transition.
3. **CREATE IO CQ `cdw11[31:16]` is the interrupt vector, not a
   CQID** (polling ⇒ 0); QEMU rejects `IV=1` with `0x1008`.

Deviations: e1000e/RTL8168/i225/i226 bind at the framework level but
their register-level datapaths are not implemented (HCL shows
`Detected` vs `Verified`); physical hardware validation is pending a
machine.

## 6. Test suite + docs (Task 6)

- `tests/run-phase9-tests.ps1` — legs `deploy`, `usb`, `ipv6`,
  `http2`, `http3`, `acpi`, `nvme`, `vbox`, `regress`. Every WSL leg
  was run against the final image while recording this report (ALL
  green); the VirtualBox leg was live-verified on this machine
  (`[PASS] vbox poweroff`, `[PASS] vbox reboot`). The `regress` leg
  re-runs the Phase 4–8 acceptance suites.
- `docs/PHASE9-ACCEPTANCE.md` — fresh-Windows-11 step-by-step for
  every acceptance criterion, with the recorded results table.
- `docs/PHASE9-HARDWARE-TESTING.md` — physical procedure with the
  11-step new-machine checklist (boot, USB keyboard/storage, NIC,
  storage, IPv6, HTTP/2, HTTP/3, poweroff, reboot).

## 7. Constraints

- All new code is managed C#; a recursive scan of kernel, bootloader,
  drivers, korlib, shell, utilities, servers, npkg and SDK finds **no
  `.c/.cpp/.cc/.h` files**.
- No native crypto/transport libraries (all TLS/QUIC/HTTP2/HPACK/QPACK
  in managed code); console-only (no GUI/framebuffer work added).
- Existing Phase 2–8 acceptance documents remain valid; the `regress`
  leg re-runs their runners.

## 8. Outstanding items

| Item | Where tracked |
|------|---------------|
| Physical x86-64 + ARM64 machine runs (checklist ready) | `PHASE9-HARDWARE-TESTING.md` |
| `curl -6` (IPv6 HTTP fetch; in-guest curl is IPv4-only) | `PHASE9-ACCEPTANCE.md` §8 |
| USB `/dev/sda` block nodes + FAT32/EXT2 mount on USB media | `PHASE9-USB.md` |
| CDC-ACM automated coverage (no QEMU CDC device) | `PHASE9-ACCEPTANCE.md` §8 |
| e1000e/RTL8168/i225/i226 register-level datapaths | `HARDWARE-COMPATIBILITY.md` |
| Guest resume after QEMU S3 wake (firmware-dependent) | `PHASE9-ACPI.md` |
| EHCI/UHCI/OHCI, USB hub downstream ports, isochronous | `PHASE9-USB.md` |
