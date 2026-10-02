#!/bin/bash
# One-off check (temp): bare `cat` prints usage, pipes still feed cat,
# and the shell no longer announces [run] lines in normal builds.
set -eu
WORK=/tmp/catcheck
mkdir -p "$WORK"
LOG=/tmp/catcheck.log
cp -f /root/run.img "$WORK/run.img"
echo 1 > /tmp/skip-boot-tests
export MTOOLS_SKIP_CHECK=1
mcopy -o -i "$WORK/run.img" /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /root/neutrino/build/x64/OVMF_VARS-catcheck.fd
rm -f /root/qin-cat "$LOG"
mkfifo /root/qin-cat

( tail -f /root/qin-cat | timeout 180 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/root/neutrino/build/x64/OVMF_VARS-catcheck.fd \
  -drive id=bootdisk,if=none,format=raw,file="$WORK/run.img" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial stdio -no-reboot -no-shutdown > "$LOG" 2>&1 ) &

echo "waiting for shell..."
for i in $(seq 1 120); do
  if strings "$LOG" 2>/dev/null | grep -q 'root-/> '; then echo "prompt at ${i}s"; break; fi
  sleep 1
done
sleep 2

cmd() { echo "$1" > /root/qin-cat; sleep "${2:-3}"; }

cmd "cat" 3
cmd "cat --help" 3
cmd "ls /bin | cat" 4

pkill -9 qemu-system 2>/dev/null || true
sleep 1

echo "=== results ==="
echo "usage-lines:      $(grep -c 'usage: cat' "$LOG" || true)"
echo "run-announce:     $(grep -c '\[run\]' "$LOG" || true)"
echo "pipe-output-lines:$(grep -c 'mkexfat.dll\|mount.dll' "$LOG" || true)"
echo "=== tail of transcript ==="
tail -40 "$LOG"
