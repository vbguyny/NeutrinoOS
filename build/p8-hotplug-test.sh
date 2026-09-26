#!/bin/bash
# Phase 8 hot-plug acceptance: boot the x64 image (q35) with an empty PCIe
# root port and a QEMU monitor socket; hot-add a virtio-net-pci through the
# monitor; assert the kernel's PCIe hot-plug detector (a) reports the root
# port at boot, (b) sees the presence-detect change, (c) adds the device to
# the tree and loads the matching driver ("[drv] bound 'virtio-net' ...");
# then device_del and assert the driver unloads ("[drv] stopped
# 'virtio-net'"). Records the wall time for device_add -> driver bound (the
# driver-load benchmark from the spec).
#
# Environment:
#   P8_HOTPLUG_IMG   image to boot (default /root/npkgtest.img; run
#                    build/p8-npkg-deploy.sh first on a fresh kernel build)
# Logs: /root/p8hotplug-serial.txt, /root/p8hotplug-monitor.txt
# Verdict: "=== hotplug summary: ALL-PASS ==="
set -uo pipefail
export MTOOLS_SKIP_CHECK=1

IMG=${P8_HOTPLUG_IMG:-/root/npkgtest.img}
SER=/root/p8hotplug-serial.txt
MON=/root/p8hotplug.mon
MONLOG=/root/p8hotplug-monitor.txt
QEMU_SECS=${P8_HOTPLUG_QEMU_SECS:-180}

PASS=0
FAIL=0
result() {
  if [ "$2" -eq 0 ]; then
    echo "PASS: $1"
    PASS=$((PASS + 1))
  else
    echo "FAIL: $1"
    FAIL=$((FAIL + 1))
  fi
}

wait_for() {
  local marker="$1" secs="$2" waited=0
  while [ "$waited" -lt "$secs" ]; do
    if grep -aq "$marker" "$SER" 2>/dev/null; then return 0; fi
    sleep 1
    waited=$((waited + 1))
  done
  return 1
}

echo "=== hotplug device acceptance ==="
test -f "$IMG" || { echo "missing $IMG - run build/p8-npkg-deploy.sh first"; exit 1; }
pkill -9 -f 'p8hotplug.mon' >/dev/null 2>&1 || true
rm -f "$SER" "$MON" "$MONLOG"

timeout -s KILL "$QEMU_SECS" qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/root/neutrino/build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file="$IMG" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -device pcie-root-port,id=hp0,slot=2 \
  -display none -serial file:"$SER" -monitor unix:"$MON",server,nowait \
  -no-reboot -no-shutdown > /dev/null 2>&1 &
QPID=$!

# ---- 1. boot + detector init -------------------------------------------
wait_for "neutrinoos> " 150
result "boot (shell prompt)" $?
wait_for "\[hotplug\] root port" 30
result "root port discovered (PCIe capability walk)" $?
wait_for "\[hotplug\] monitoring" 30
result "hot-plug poll armed" $?
grep -aq "slot=yes" "$SER"
result "slot registers present (slot=yes)" $?

# ---- 2. hot-add a virtio-net-pci through the monitor --------------------
# The python helper records the exact send timestamp so the measured
# load time spans monitor command -> driver bound (no harness sleeps).
STAMP=/tmp/p8hp-sent
date +%s.%N > "$STAMP"
python3 - "$MON" "$MONLOG" "$STAMP" <<'PY'
import socket, sys, time
mon, log, stamp = sys.argv[1], sys.argv[2], sys.argv[3]
s = socket.socket(socket.AF_UNIX)
s.connect(mon)
t = time.time()
s.sendall(b"device_add virtio-net-pci,id=hpnic0,bus=hp0\n")
open(stamp, "w").write("%.6f" % t)
time.sleep(0.3)
try:
    s.settimeout(1.0)
    data = s.recv(65536)
except socket.timeout:
    data = b""
open(log, "ab").write(b"device_add: " + data + b"\n")
s.close()
PY
t0=$(cat "$STAMP")

wait_for "\[hotplug\] device added" 30
result "device added to tree (bus rescan)" $?
wait_for "\[drv\] bound 'virtio-net'" 30
if [ $? -eq 0 ]; then
  t1=$(date +%s.%N)
  LOADMS=$(awk -v a="$t0" -v b="$t1" 'BEGIN { printf "%.0f", (b-a)*1000 }')
  echo "PASS: driver bound (virtio-net, ${LOADMS} ms after device_add)"
  PASS=$((PASS + 1))
else
  echo "FAIL: driver bound (no '[drv] bound' within 30s)"
  FAIL=$((FAIL + 1))
fi
! grep -aq "no driver matched" "$SER"
result "matching driver found" $?

# ---- 3. hot-remove the device ------------------------------------------
python3 - "$MON" "$MONLOG" "$STAMP" <<'PY'
import socket, sys, time
mon, log, stamp = sys.argv[1], sys.argv[2], sys.argv[3]
s = socket.socket(socket.AF_UNIX)
s.connect(mon)
t = time.time()
s.sendall(b"device_del hpnic0\n")
open(stamp, "w").write("%.6f" % t)
time.sleep(0.3)
try:
    s.settimeout(1.0)
    data = s.recv(65536)
except Exception:
    data = b""
open(log, "ab").write(b"device_del: " + data + b"\n")
s.close()
PY
t2=$(cat "$STAMP")

wait_for "\[hotplug\] device removed" 30
result "device removed from tree" $?
wait_for "\[drv\] stopped 'virtio-net'" 30
if [ $? -eq 0 ]; then
  t3=$(date +%s.%N)
  UNLDMS=$(awk -v a="$t2" -v b="$t3" 'BEGIN { printf "%.0f", (b-a)*1000 }')
  echo "PASS: driver stopped (virtio-net, ${UNLDMS} ms after device_del)"
  PASS=$((PASS + 1))
else
  echo "FAIL: driver stopped (no '[drv] stopped' within 30s)"
  FAIL=$((FAIL + 1))
fi

# Kill QEMU by its monitor path: killing the `timeout` wrapper pid would
# orphan the qemu process, which then keeps the image write lock forever.
pkill -9 -f 'p8hotplug.mo[n]' 2>/dev/null || true

echo "=== hotplug summary: $PASS PASS / $FAIL FAIL ==="
if [ "$FAIL" -eq 0 ]; then
  echo "=== hotplug summary: ALL-PASS ==="
  exit 0
fi
echo "=== hotplug summary: HAS-FAILURES ==="
echo "--- serial tail ---"
tail -40 "$SER"
echo "--- monitor log ---"
cat "$MONLOG" 2>/dev/null || true
exit 1
