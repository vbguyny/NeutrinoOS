#!/bin/bash
# Boot once and capture the AML table dump for offline iasl disassembly.
set -uo pipefail
LOG=/root/p9dump.log
rm -f "$LOG" /tmp/p9d.in /tmp/p9d.out
pkill -9 -f 'p9dump-qem[u]' 2>/dev/null || true
sleep 1
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd /tmp/p9dv.fd
mkfifo /tmp/p9d.in /tmp/p9d.out

qemu-system-x86_64 -name p9dump-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9dv.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/neutrino/build/x64/neutrinoos.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial pipe:/tmp/p9d > /dev/null 2>&1 &
QPID=$!
cat /tmp/p9d.out > "$LOG" &
CATPID=$!

for i in $(seq 1 90); do
  grep -aq '\[amldump3\] end' "$LOG" 2>/dev/null && break
  sleep 1
done
sleep 3
kill -9 "$QPID" "$CATPID" 2>/dev/null || true
pkill -9 -f 'p9dump-qem[u]' 2>/dev/null || true
echo "tables dumped: $(grep -ac 'amldump1' "$LOG")"
echo "dump lines: $(grep -ac 'amldump2' "$LOG")"
