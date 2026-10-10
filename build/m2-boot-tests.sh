#!/usr/bin/env bash
# Boot the deploy image (boot tests ENABLED - no skip marker) and report the
# JITTest results, focused on the new Memory/Pipelines category (Kestrel M2).
set -u
cd /mnt/d/Projects/Code/NeutrinoOS
cp /root/run.img /tmp/m2-run.img
cp /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/m2-vars.fd
rm -f /tmp/m2-serial.log

qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/m2-vars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/tmp/m2-run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=net0 \
  -device virtio-net-pci,netdev=net0,disable-legacy=on \
  -display none -serial file:/tmp/m2-serial.log -no-reboot -no-shutdown \
  >/dev/null 2>&1 &
QPID=$!

# Wait for the final test summary (JITTest then AppTest run during boot;
# seeing AppTest Results implies the JITTest section has finished too).
for i in $(seq 1 900); do
  if grep -aq 'AppTest Results' /tmp/m2-serial.log 2>/dev/null; then
    sleep 2
    break
  fi
  sleep 0.5
done

kill $QPID 2>/dev/null || true
sleep 1

echo "=== JITTest summary:"
grep -a -e '\[JITTest\]' /tmp/m2-serial.log | tail -8
echo ""
echo "=== Memory/Pipelines category output:"
grep -a -A 60 'CATEGORY. Memory/Pipelines' /tmp/m2-serial.log | head -60
echo ""
echo "=== category errors (if any):"
grep -a 'CATEGORY ERROR' /tmp/m2-serial.log | head -10
echo ""
echo "=== all FAIL lines (should be none):"
grep -a -e 'FAIL' -e 'Failed' /tmp/m2-serial.log | grep -a -v -e 'Failed: 0' | head -20
echo ""
echo "=== boot timeline tail:"
grep -a '\[Boot\]' /tmp/m2-serial.log | tail -4
