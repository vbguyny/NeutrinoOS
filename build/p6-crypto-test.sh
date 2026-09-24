#!/bin/bash
# Phase 6: managed crypto KAT run (boots the guest, runs cryptotest).
set -u
cd /root/neutrino
pkill -9 qemu-system 2>/dev/null || true
sleep 1

cleanup() {
  pkill -9 -f qemu-system 2>/dev/null || true
  rm -f /root/qin
}
trap cleanup EXIT

bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-deploy.sh > /root/p6deploy.log 2>&1
sleep 1
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin /root/p6crypto.log
mkfifo /root/qin

( tail -f /root/qin | timeout 150 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0 -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p6crypto.log 2>&1 ) &

echo "waiting for shell..."
for i in $(seq 1 90); do
  if strings /root/p6crypto.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; break; fi
  sleep 1
done
sleep 2

printf 'cryptotest\n' > /root/qin
for i in $(seq 1 60); do
  if strings /root/p6crypto.log 2>/dev/null | grep -q '\[cryptotest\] PASS\|\[cryptotest\] FAIL\|command not found'; then break; fi
  sleep 1
done

# Everything after the cryptotest invocation line is the utility's output.
strings /root/p6crypto.log | awk '/neutrinoos> cryptotest/{f=1} f' > /root/p6crypto-seg.log
echo "=== cryptotest output:"
cat /root/p6crypto-seg.log | tail -45

fails=$(grep -ac '\[FAIL\]' /root/p6crypto-seg.log || true)
passes=$(grep -ac '\[PASS\]' /root/p6crypto-seg.log || true)
echo "=== summary: PASS=$passes FAIL=$fails"
crash=$(strings /root/p6crypto.log | grep -sc 'SYSTEM HALTED' || true)
echo "SYSTEM HALTED: $crash"
