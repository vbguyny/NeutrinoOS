#!/bin/bash
# Boot NeutrinoOS for the history-redraw check (build/history-vga-check.ps1).
# Copies the plain kernel image to /tmp, adds the skip-boot-tests marker,
# then starts QEMU detached: VGA on std, serial on a FIFO (for serial-driven
# input), monitor on TCP for sendkey/xp queries.
#
# Usage: bash build/history-vga-boot.sh [monitor-port]
set -u
PORT="${1:-5599}"
cd /root/neutrino
pkill -9 -f qin-hist 2>/dev/null || true
pkill -9 -f qemu-system 2>/dev/null || true
sleep 1
rm -f /root/qin-hist /root/hist-qemu.out
cp -f build/x64/neutrinoos.img /tmp/hist-check.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-hist.fd
echo 1 > /tmp/skip-boot-tests
mdel -i /tmp/hist-check.img ::/skip-boot-tests ::/run-console-test 2>/dev/null || true
mcopy -o -i /tmp/hist-check.img /tmp/skip-boot-tests ::/skip-boot-tests
mkfifo /root/qin-hist
# Disk attachment: ide.0 (q35 ich9 AHCI). The kernel's boot-file helpers
# (FileExports -> AhciEntry.BootPathExists/ReadBootFile/...) are hard-wired to
# the AHCI driver, so a virtio-blk boot disk makes every korlib File/Directory
# call fail (e.g. "cd /apps: no such directory") even though UEFI boots fine.
setsid bash -c "tail -f /root/qin-hist | qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-hist.fd \
  -drive id=histdisk,if=none,format=raw,file=/tmp/hist-check.img \
  -device ide-hd,drive=histdisk,bus=ide.0 \
  -vga std -display none -serial stdio \
  -monitor tcp:127.0.0.1:$PORT,server,nowait \
  -no-reboot -no-shutdown" < /dev/null > /root/hist-qemu.out 2>&1 &
sleep 2
if pgrep -f qemu-system > /dev/null; then echo "started"; else echo "FAILED TO START"; exit 1; fi
