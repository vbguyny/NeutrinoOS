#!/bin/bash
# Boot the guest paused (-s -S) for GDB debugging of the DHCPv6 GP fault.
set -u
cd /root/neutrino

pkill -9 -f 'p9dbgv6-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9dbgv6-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9dbgv6.in /root/p9dbgv6.log
mkfifo /root/p9dbgv6.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9dbgv6vars.fd

( tail -f /root/p9dbgv6.in | qemu-system-x86_64 -name p9dbgv6-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9dbgv6vars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown -s -S > /root/p9dbgv6.log 2>&1 ) &
echo "qemu started (paused)"
