# NeutrinoOS Phase 9 — Acceptance Verification

Step-by-step verification for every Phase 9 acceptance criterion, from a
fresh Windows 11 machine. Everything is machine-checkable;
`tests/run-phase9-tests.ps1` runs the whole set in order.

## Acceptance results (recorded on this codebase)

| # | Criterion (spec) | Evidence | Result |
|---|------------------|----------|--------|
| 1 | `make image` / `make run-qemu-vga` boot to the banner on both consoles, no Phase 8 regressions | standard image boots to `[SHELL] NeutrinoOS console ready.` + `neutrinoos>`; GUI/VGA via `scripts/gui-vm.ps1` / `make run-qemu-vga`; the `regress` leg re-runs the Phase 4–8 acceptance suites | ✅ |
| 2 | `make run-qemu-usb` boots with xHCI; USB keyboard works | `build/p9-usb-test.sh` **11 PASS / 0 FAIL**: xHCI running, HID keyboard bound, and typing `version` through the USB keyboard runs the shell command end-to-end (QEMU `sendkey`) | ✅ |
| 3 | USB mass storage appears and can be read | same suite: BOT mass storage enumerated as disk `sda`, listed by the `usb` builtin, READ CAPACITY reports a sector count. `/dev/sda` VFS block nodes + FAT32/EXT2 mount on USB media are tracked with the VFS unification work (documented in `PHASE9-USB.md`) | ⚠️ partial |
| 4 | USB CDC-ACM appears as a serial device, R/W works | driver implemented (control 0x02/0x02 + data 0x0A, ports `ttyUSB0…`, polled RX ring, `UsbSerial.Write`). QEMU provides no CDC-ACM device (its `usb-serial` is FTDI vendor-specific), so automated coverage is deferred; covered by the physical checklist with real adapters | ⚠️ deferred (implemented) |
| 5 | USB hot-plug loads/unloads drivers + nodes | same suite: `device_add usb-kbd` enumerates a second keyboard; `device_del` unbinds the driver and disables the slot | ✅ |
| 6 | `ifconfig` shows an IPv6 address (SLAAC/DHCPv6) | `build/p9-ipv6-acc3.sh` **PASS=8 FAIL=0**: SLAAC address + router learned, DHCPv6 INFORMATION-REQUEST answered, `IPv6 DNS:` learned | ✅ |
| 7 | `ping6 ::1` succeeds | same suite: `2 packets transmitted, 2 packets received` (loopback through the real build/parse/checksum path) | ✅ |
| 8 | `curl -6 http://[::1]:5000/` fetches from the web server over IPv6 | in-guest `curl` is IPv4-only; `ping6 ::1` + ICMPv6 verified. v6 HTTP client fetch deferred (deviation below) | ⚠️ deferred |
| 9 | `curl --http2 https://...` fetches over HTTP/2 | `build/p9-h2-tap.sh`: host `curl -k --http2 https://10.0.2.15/health` → `< HTTP/2 200`, ALPN `h2`; plus in-guest `h2test` 12/12 and h2c upgrade/prior-knowledge | ✅ |
| 10 | `curl --http3 https://...` fetches over HTTP/3 (QUIC) | `build/p9-quic-test.sh`: real QUIC client (aioquic) — handshake complete ALPN `h3`, `GET /health` → `200`, `GET /` → `200` (`RESULT: PASS` ×2). Host curl lacks HTTP/3 (WSL build) — deviation below | ✅ |
| 11 | `poweroff` shuts down cleanly on VirtualBox and real hardware (QEMU may not support it — document) | QEMU: `build/p9-acpi-test.sh` **18 PASS / 0 FAIL** (S5 via PM1_CNT + `\_S5` SLP_TYP; QEMU exits by itself); VirtualBox: `scripts/test-vbox-power.ps1` `[PASS] vbox poweroff` (live-verified, VM state `poweroff`); real hardware: procedure in `PHASE9-HARDWARE-TESTING.md` (pending a machine) | ✅ (real HW pending) |
| 12 | `reboot` reboots cleanly on VirtualBox and real hardware | QEMU: same suite (`boot banner appears again` in the same process); VirtualBox: `[PASS] vbox reboot` (second `NeutrinoOS v…` banner in the serial log) | ✅ (real HW pending) |
| 13 | `sleep` enters S3 and wakes (may be deferred; document) | QEMU: S3 entry reported with `\_S3` SLP_TYP; monitor reports VM suspended; `system_wakeup` flips it back to running. Guest-side resume after wake is firmware-dependent on QEMU — documented in `PHASE9-ACPI.md` | ✅ (with documented QEMU caveat) |
| 14 | CPU C-states / P-states reported via `cpupower` | QEMU: `cpupower` C-state/P-state report lines asserted by the ACPI suite | ✅ |
| 15 | Boots on a physical x86-64 machine (USB kbd, NIC, NVMe/SATA) to a shell | not performed (no physical machine attached); tooling ready: `scripts/flash-usb.ps1` + the 11-step checklist in `PHASE9-HARDWARE-TESTING.md`; QEMU/VBox cover the same code paths | ⚠️ pending HW |
| 16 | Boots on a physical ARM64 machine (may be deferred; document) | deferred (no hardware); ARM64 kernel from Phase 8 still boots under QEMU (Phase 8 regress leg) | ⚠️ deferred |
| 17 | `docs/HARDWARE-COMPATIBILITY.md` lists tested hardware + status | present, with honest per-family statuses (verified/detected/not supported) | ✅ |
| 18 | No C/C++ files in kernel, bootloader, drivers, korlib, shell, utilities, servers, npkg, SDK | recursive scan of those trees: **no `.c/.cpp/.cc/.h` files** | ✅ |
| 19 | Earlier docs still work; `PHASE9-ACCEPTANCE.md` gives step-by-step verification | this document + `tests/run-phase9-tests.ps1`; `regress` leg re-runs Phase 4–8 suites | ✅ |

Deviations and pending items are collected in `PHASE9-REPORT.md`.

## 0. Prerequisites (fresh Windows 11)

1. WSL2 + Ubuntu 24.04 with the Phase 1–8 toolchain
   (`docs/BUILD-WINDOWS.md` §1–3), plus:

   ```bash
   sudo apt install -y qemu-system-x86 qemu-system-arm ovmf mtools \
                       python3 python3-pip rsync
   pip3 install --break-system-packages aioquic      # HTTP/3 client
   ```

2. The WSL build tree with the Makefile patch for the NVMe driver is
   applied automatically by the repository scripts; the build entry
   point is `/root/p9-build.sh` (rsyncs `src/`, runs `make image`).

3. Optional: VirtualBox 7.x on Windows for the `vbox` leg
   (skipped automatically when absent).

## 1. One-shot run

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-phase9-tests.ps1
```

Legs: `deploy`, `usb`, `ipv6`, `http2`, `http3`, `acpi`, `nvme`,
`vbox`, `regress`. Run a subset with `-Only`, e.g.:

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-phase9-tests.ps1 -Only usb,nvme
```

The summary table ends with `ALL-PASS (n checks)` on success.

## 2. What each leg does

| Leg | Command (inside WSL unless noted) | Verdict string |
|-----|-----------------------------------|----------------|
| deploy | `bash /root/p9-build.sh` + `build/p5-apps-build.sh` + `build/p5-deploy.sh` | `/root/run.img ::/drivers` contains the NVMe DLL |
| usb | `bash build/p9-usb-test.sh` | `=== usb summary: ALL-PASS ===` |
| ipv6 | `bash build/p9-ipv6-acc3.sh` | `PASS=8 FAIL=0` |
| http2 | `bash build/p9-h2test.sh`, `bash build/p9-h2-tap.sh` | `h2test: PASS` ×13; all `rc=0`, `HTTP/2 200` |
| http3 | `bash build/p9-quic-test.sh` | `RESULT: PASS` (aioquic, `status: 200`) |
| acpi | `bash build/p9-acpi-test.sh` | `=== acpi summary: ALL-PASS ===` (18 checks) |
| nvme | `bash build/p9-nvme-test.sh` | `NVME: PASS` |
| vbox | `powershell -File scripts\test-vbox-power.ps1` | `[PASS] vbox poweroff` + `[PASS] vbox reboot` |
| regress | `tests\run-phase4-tests.ps1` … `run-phase8-tests.ps1` | per-phase exit code 0 |

## 3. USB — manual spot checks

```bash
bash build/p9-usb-test.sh          # full automated run
# manual: QEMU comes up with -device qemu-xhci,usb-kbd,usb-storage; then
#   usb            -> controller, HID keyboard, mass storage, serial list
#   type on the QEMU window: keystrokes echo at the shell
```

## 4. IPv6 — manual spot checks

Boot with the IPv6-enabled netdev (`build/run-qemu-usb.sh` or
`build/p9-ipv6-test.sh`), then at the shell:

```
ifconfig eth0 up      # RS/RA exchange -> SLAAC address + router
dhcp6                 # stateless INFORMATION-REQUEST -> IPv6 DNS
ping6 -c 2 ::1        # loopback
ping6 -c 3 fe80::2    # NDP + ICMPv6 to the slirp router
dns6 <name>           # dual-stack resolution path
```

## 5. HTTP/2 + HTTP/3 — manual spot checks

The TAP matrix (host owns `10.0.2.2/24` on `n0tap`, guest `10.0.2.15`):

```
webhost start                 # guest: plain :80 + TLS :443 + QUIC :443
curl -sS -v --http1.1 http://10.0.2.15/health
curl -sS -v --http2-prior-knowledge http://10.0.2.15/
curl -sS -v --http2 http://10.0.2.15/health         # h2c upgrade
curl -sS -k -v --http2 https://10.0.2.15/health     # ALPN h2
python3 build/p9-h3-client.py 10.0.2.15 443 /health # QUIC client
```

Note: the implementation serves HTTP on 80/443 (not 5000/5001); the
criterion's semantics (fetch over HTTP/2 and HTTP/3) are what is
verified.

## 6. Power management — manual spot checks

```
poweroff      # ACPI S5: QEMU exits by itself; VBox VM state -> poweroff
reboot        # resets; the boot banner appears again
sleep         # S3 entry (QEMU: monitor shows suspended; wake via system_wakeup)
cpupower      # C-state/P-state report
```

## 7. Physical machine

Follow `docs/PHASE9-HARDWARE-TESTING.md` (flash, UEFI setup, serial
capture) and work through its 11-step new-machine checklist. Record
findings in `docs/HARDWARE-COMPATIBILITY.md`.

## 8. Known deviations

- **Real-hardware legs pending**: no physical x86-64/ARM64 machine was
  attached for this recording; the automated suites cover QEMU
  (xHCI/USB, IPv6 over virtio-net, HTTP/2+3, ACPI S5/S3, NVMe) and
  VirtualBox (poweroff/reboot).
- **USB storage VFS nodes**: the disk is enumerated and its capacity is
  read (`sda`, sector count); `/dev/sda` block nodes + FAT32/EXT2 mount
  on USB media are tracked with the VFS unification work, and the
  in-kernel FAT path currently serves the internal disk
  (`docs/PHASE9-USB.md`).
- **USB CDC-ACM coverage**: implemented (control 0x02/0x02 + data
  0x0A, `ttyUSB0…` ports); automated QEMU coverage deferred because
  QEMU offers no CDC-ACM device (only FTDI). Physical adapters are in
  the new-machine checklist.
- **EHCI/UHCI/OHCI, USB hubs downstream, isochronous transfers**:
  documented deferred in `docs/PHASE9-USB.md`.
- **`curl -6` (criterion 8)**: the in-guest `curl` utility speaks IPv4
  only; IPv6 is verified at the ICMPv6/NDP/DHCPv6/SLAAC/DNS layers.
  Fetches over IPv6 need an IPv6 socket path in `curl`.
- **`curl --http3`**: the WSL curl build has no HTTP/3 support, so the
  acceptance uses a real QUIC client (aioquic) instead; see
  `docs/PHASE9-HTTP2-3.md`.
- **Port numbering**: web service ports are 80/443 (spec text mentions
  5000/5001; the delivered service uses the standard ports).
- **QEMU S3 guest resume**: entry and monitor-level wake verified;
  guest continuation after wake is firmware-dependent on QEMU (see
  `docs/PHASE9-ACPI.md`).
- **NIC families**: e1000e/RTL8168/i225 bind at the PCI framework
  level; datapaths beyond the verified virtio-net/e1000 path are
  tracked in `docs/HARDWARE-COMPATIBILITY.md`.
