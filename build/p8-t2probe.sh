#!/bin/bash
# Task 2 device-tree boot probe: deploy a fresh npkg image, boot it headless
# and print the [drv]/[DeviceTree] boot lines. Log: /root/p8t2.log
set -uo pipefail
exec < /dev/null
cd /root/neutrino

IMG=/root/npkgtest.img
LOG=/root/p8t2.log
FIFO=/root/p8t2in

pkill -9 -f 'qemu-system-x86_64.*npkgtes[t]' 2>/dev/null || true
sleep 1

rm -f "$LOG" "$FIFO"
mkfifo "$FIFO"
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd

( tail -f "$FIFO" | timeout -s KILL 420 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
    -drive id=bootdisk,if=none,format=raw,file="$IMG" \
    -device ide-hd,drive=bootdisk,bus=ide.0 \
    -display none -serial stdio -no-reboot -no-shutdown > "$LOG" 2>&1 ) &

waited=0
while [ "$waited" -lt 150 ]; do
  if grep -aq 'neutrinoos> ' "$LOG" 2>/dev/null; then break; fi
  sleep 2
  waited=$((waited + 2))
done
sleep 2

echo "[t2] === driver framework boot lines ==="
grep -a '\[drv\]\|\[DeviceTree\]' "$LOG" | head -50
echo "[t2] === boot status tail ==="
grep -a 'Boot\]' "$LOG" | tail -6
echo "[t2] === fatal check ==="
grep -a -c 'RAWV\|EH\] FATAL' "$LOG" 2>/dev/null || echo 0

pkill -9 -f 'qemu-system-x86_64.*npkgtes[t]' 2>/dev/null || true
pkill -9 -f 'tail -f /root/p8t2i[n]' 2>/dev/null || true
echo "[t2] done"
