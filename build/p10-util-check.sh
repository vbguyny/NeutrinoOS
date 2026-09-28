#!/bin/bash
# One-off check (temp): run a broad set of utilities and scan the serial
# transcript for stray JIT trace fragments in a normal (non-trace) build.
set -eu
WORK=/tmp/utilcheck
mkdir -p "$WORK"
LOG=/tmp/utilcheck.log
cp -f /root/run.img "$WORK/run.img"
echo 1 > /tmp/skip-boot-tests
export MTOOLS_SKIP_CHECK=1
mcopy -o -i "$WORK/run.img" /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /root/neutrino/build/x64/OVMF_VARS-utilcheck.fd
rm -f /root/qin-util "$LOG"
mkfifo /root/qin-util

( tail -f /root/qin-util | timeout 240 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/root/neutrino/build/x64/OVMF_VARS-utilcheck.fd \
  -drive id=bootdisk,if=none,format=raw,file="$WORK/run.img" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial stdio -no-reboot -no-shutdown > "$LOG" 2>&1 ) &

for i in $(seq 1 120); do
  if strings "$LOG" 2>/dev/null | grep -q 'neutrinoos> '; then break; fi
  sleep 1
done
sleep 2

cmd() { echo "$1" > /root/qin-util; sleep "${2:-2}"; }

cmd "echo hello > /test.txt"
cmd "cat /test.txt"
cmd "ls /bin" 3
cmd "ls /apps"
cmd "cp /test.txt /copy.txt"
cmd "cat /copy.txt"
cmd "mv /copy.txt /moved.txt"
cmd "cat /moved.txt"
cmd "rm /moved.txt"
cmd "touch /t.txt"
cmd "head /test.txt"
cmd "tail /test.txt"
cmd "wc /test.txt"
cmd "grep hello /test.txt"
cmd "find /" 3
cmd "uname"
cmd "date"
cmd "uptime"
cmd "free"
cmd "env"
cmd "true"
cmd "ps"
cmd "df"
cmd "gc" 4
cmd "cpupower" 3
cmd "usb" 3
cmd "cp"
cmd "mv"
cmd "rm"
cmd "head /test.txt"
cmd "tail /test.txt"
cmd "wc /test.txt"
cmd "grep hello /test.txt"
cmd "find /bin" 3
cmd "kill 99999"
cmd "exit" 2

pkill -9 qemu-system 2>/dev/null || true
sleep 1

echo "=== suspicious non-bracket hex lines post-prompt ==="
awk '/neutrinoos> /{seen=1} seen' "$LOG" | grep -n '0x[0-9A-Fa-f]\{3,\}' | grep -v ':\[[A-Za-z]' | head -40 || true
echo "=== bracketed post-prompt lines (incl. indented) ==="
awk '/neutrinoos> /{seen=1} seen' "$LOG" | grep -E '^[[:space:]]*\[' | sort -u | head -40 || true
echo "=== stray literal markers ==="
awk '/neutrinoos> /{seen=1} seen' "$LOG" | grep -E 'Suspending NeutrinoOS|GENINST|VAR-FLD|resolved from|StackRoot|frames,' | sort -u | head -40 || true
echo "=== end ==="
