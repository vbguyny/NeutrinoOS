#!/bin/bash
# Boot the guest and run dbgtest to isolate DDK Debug P/Invoke behavior.
set -u
cd /root/neutrino

pkill -9 -f 'p9dbg-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9dbg-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9dbg.in /root/p9dbg.log
mkfifo /root/p9dbg.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9dbgvars.fd

( tail -f /root/p9dbg.in | timeout 180 qemu-system-x86_64 -name p9dbg-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9dbgvars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9dbg.log 2>&1 ) &

ok=0
for i in $(seq 1 120); do
  if strings /root/p9dbg.log 2>/dev/null | grep -q 'root-/> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9dbg.log; exit 1; fi
sleep 2

echo "dbgtest" > /root/p9dbg.in
sleep 8
pkill -9 -f 'p9dbg-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9dbg.log > /root/p9dbg.txt
echo "=== dbgtest output ==="
grep -a 'dbgtest' /root/p9dbg.txt | head -30
echo "=== faults ==="
grep -a 'RAWV\|v=0x' /root/p9dbg.txt | head -5
