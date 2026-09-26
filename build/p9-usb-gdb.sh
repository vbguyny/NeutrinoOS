#!/bin/bash
# Boot under GDB, break at the GP fault instruction, disassemble.
set -uo pipefail
STICK=/root/p9usb-stick.img
rm -f /tmp/p9g.ser.in /tmp/p9g.ser.out
pkill -9 -f 'p9gdb-qem[u]' 2>/dev/null || true
pkill -9 -f 'gdb.*1234' 2>/dev/null || true
sleep 1
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd /tmp/p9gv.fd
mkfifo /tmp/p9g.ser.in /tmp/p9g.ser.out

qemu-system-x86_64 -name p9gdb-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9gv.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/neutrino/build/x64/neutrinoos.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -device qemu-xhci -device usb-kbd \
  -drive id=usbstick,if=none,format=raw,file="$STICK" \
  -device usb-storage,drive=usbstick \
  -display none -serial pipe:/tmp/p9g.ser \
  -s -S -no-reboot -no-shutdown > /dev/null 2>&1 &
QPID=$!
cat /tmp/p9g.ser.out > /tmp/p9gdb.log &
CATPID=$!
sleep 2

timeout 90 gdb -batch -q \
  -ex "set confirm off" \
  -ex "target remote :1234" \
  -ex "hbreak *0x80A8C60" \
  -ex "continue" \
  -ex "printf \"=== REGISTERS AT HIT ===\\n\"" \
  -ex "info registers rax rbx rcx rdx rsi rdi rbp rsp r8 r9 r10 r11 r12 r13 r14 r15 rip" \
  -ex "printf \"=== CODE ===\\n\"" \
  -ex "x/24i \$rip-0x40" \
  -ex "printf \"=== STACK ===\\n\"" \
  -ex "x/16gx \$rsp" \
  -ex "printf \"=== R12/R15 AREAS ===\\n\"" \
  -ex "x/4gx \$r12" \
  -ex "x/8gx \$r15-0x10" \
  -ex "detach" 2>&1 | tail -70

kill -9 "$QPID" "$CATPID" 2>/dev/null || true
pkill -9 -f 'p9gdb-qem[u]' 2>/dev/null || true
