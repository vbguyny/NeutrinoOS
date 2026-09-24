#!/bin/bash
# Phase 7: boot with the current image - capture boottime and jitstats.
# usage: p7-bootmeasure.sh [image]   (default build/x64/neutrinoos.img)
set -u
cd /root/neutrino
IMG="${1:-build/x64/neutrinoos.img}"
exec < /dev/null
echo "[$(date +%T)] p7-bootmeasure start ($IMG)"
pkill -9 qemu-system 2>/dev/null || true
sleep 1

cleanup() {
  pkill -9 -f qemu-system 2>/dev/null || true
  rm -f /root/qin
}
trap cleanup EXIT

cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin /root/p7bootmeas.log
mkfifo /root/qin

( tail -f /root/qin | timeout 180 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file="$IMG" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p7bootmeas.log 2>&1 ) &

BOOT_START=$(date +%s)
for i in $(seq 1 150); do
  if strings /root/p7bootmeas.log 2>/dev/null | grep -q 'neutrinoos> '; then
    echo "prompt at $(( $(date +%s) - BOOT_START ))s"
    break
  fi
  sleep 1
done
sleep 2

send() {
  timeout 5 bash -c "printf '%s\n' '$1' > /root/qin" 2>/dev/null || echo "!! guest input fifo blocked"
  sleep "$2"
}

send "boottime" 4
send "jitstats" 8

echo "===== boottime ====="
strings /root/p7bootmeas.log | awk '/Boot.Status timeline/{f=1} f' | head -26
echo "===== jitstats ====="
strings /root/p7bootmeas.log | awk '/jitstats. methods compiled/{f=1} f' | grep -aE 'jitstats' | head -4
echo "===== serial lines ====="
strings /root/p7bootmeas.log | wc -l
echo "===== halted ====="
strings /root/p7bootmeas.log | grep -ac 'SYSTEM HALTED' || true
echo "[$(date +%T)] p7-bootmeasure done"
