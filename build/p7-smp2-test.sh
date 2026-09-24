#!/bin/bash
# Boot the current dev image with 2 vCPUs in QEMU to reproduce the
# VBox 2-vCPU deadlock offline (bootloop uses -smp 1).
set -u
cd /root/neutrino
pkill -9 qemu-system 2>/dev/null || true
sleep 1
ip="${1:-build/x64/neutrinoos.img}"
echo "image: $ip"
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/smp2.log
timeout -s KILL 150 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 2 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file="$ip" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial file:/root/smp2.log -no-reboot -no-shutdown > /dev/null 2>&1
echo "=== tail:"
strings /root/smp2.log | tail -15
echo "=== markers:"
strings /root/smp2.log | grep -cE '\[SEC\]'
strings /root/smp2.log | grep -a 'neutrinoos> ' | head -1
strings /root/smp2.log | grep -acE 'X64 Exception|SYSTEM HALTED'
