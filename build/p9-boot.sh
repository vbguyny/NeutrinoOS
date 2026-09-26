#!/bin/bash
# Boot once to the shell, capture serial; optionally send a command.
# usage: p9-boot.sh [logfile] [command-to-send]
set -uo pipefail
LOG=${1:-/root/p9boot.log}
CMD=${2:-}
rm -f "$LOG" /tmp/p9b.in /tmp/p9b.out
pkill -9 -f 'p9boot-qem[u]' 2>/dev/null || true
sleep 1
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd /tmp/p9bv.fd
mkfifo /tmp/p9b.in /tmp/p9b.out

qemu-system-x86_64 -name p9boot-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9bv.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/neutrino/build/x64/neutrinoos.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial pipe:/tmp/p9b > /dev/null 2>&1 &
QPID=$!
cat /tmp/p9b.out > "$LOG" &
CATPID=$!

for i in $(seq 1 240); do
  grep -aq 'neutrinoos> ' "$LOG" 2>/dev/null && break
  sleep 1
done
echo "booted: $(grep -ac 'neutrinoos> ' "$LOG" 2>/dev/null)"

if [ -n "$CMD" ]; then
  printf '%s\r' "$CMD" > /tmp/p9b.in
  sleep 6
fi

kill -9 "$QPID" "$CATPID" 2>/dev/null || true
pkill -9 -f 'p9boot-qem[u]' 2>/dev/null || true
grep -a '\[AML\]\|\[power\]\|\[cpupower\]' "$LOG" | head -30
