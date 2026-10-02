#!/bin/bash
# CWD-relative utility check: boots the CLI image (needs /bin utilities)
# and verifies that commands/utilities resolve paths from the shell's
# current directory (shared cwd bridge: Platform.FileExports
# DirBootGetCwd/DirBootSetCwd <-> korlib Directory.Get/SetCurrentDirectory):
#   - cd /etc; ls         -> bare `profile` line (ls lists the cwd)
#   - pwd                 -> /etc
#   - cat profile         -> file content (relative path read)
#   - ls ..               -> `apps` (parent-relative path)
#   - cd /apps; find -name p10*   -> p10hello (find defaults to the cwd)
#   - run p10hello.dll    -> 'Hello, Again!' (relative `run`)
#   - no 'no such directory' errors
#
# Usage: bash build/cwd-qemu-check.sh [image]   (default: CLI image)
set -u
IMG="${1:-build/x64/neutrinoos-cli.img}"

cd /root/neutrino || exit 1
pkill -9 -f qin-cwd 2>/dev/null || true
pkill -9 -f qemu-system 2>/dev/null || true
sleep 1

rm -f /root/qin-cwd /root/cwd-qemu.out
cp -f "$IMG" /tmp/cwd-check.img || { echo "image missing: $IMG"; exit 1; }

if [ -z "$(mdir -i /tmp/cwd-check.img ::/bin 2>/dev/null | head -1)" ]; then
    echo "image has no /bin utilities - use the CLI image"; exit 1
fi

cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-cwd.fd
echo 1 > /tmp/cwd-skip-marker
mdel -i /tmp/cwd-check.img ::/skip-boot-tests ::/run-console-test 2>/dev/null || true
mcopy -o -i /tmp/cwd-check.img /tmp/cwd-skip-marker ::/skip-boot-tests

mkfifo /root/qin-cwd
setsid bash -c "tail -f /root/qin-cwd | qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-cwd.fd \
  -drive id=cwddisk,if=none,format=raw,file=/tmp/cwd-check.img \
  -device ide-hd,drive=cwddisk,bus=ide.0 \
  -vga std -display none -serial stdio \
  -no-reboot -no-shutdown" < /dev/null > /root/cwd-qemu.out 2>&1 &

LOG=/root/cwd-qemu.out
echo "[CWD] waiting for the shell prompt..."
for i in $(seq 1 120); do
    if grep -q 'root-/>' "$LOG" 2>/dev/null; then break; fi
    sleep 1
done

send() { printf '%s\r' "$1" > /root/qin-cwd; sleep 2; }

send 'cd /etc'
send 'ls'
send 'pwd'
send 'cat profile'
send 'ls ..'
send 'cd /apps'
send 'find -name p10*'
send 'run p10hello.dll'
send 'Again'
send 'cd /'

sleep 2
pkill -9 -f qin-cwd 2>/dev/null || true
pkill -9 -f qemu-system 2>/dev/null || true
sleep 1

fail=0
CLEAN=/root/cwd-qemu-clean.log
tr -d '\r' < "$LOG" > "$CLEAN"
check() { # $1=description, $2=extended-regex
    if grep -qE "$2" "$CLEAN"; then
        echo "  [PASS] $1"
    else
        echo "  [FAIL] $1 (missing: $2)"
        fail=$((fail + 1))
    fi
}

echo
echo "[CWD] results (image: $IMG)"
check "cd /etc tracks the prompt (root-/etc>)" 'root-/etc>'
check "ls with no args lists the cwd (/etc -> profile)" '^profile$'
check "pwd prints /etc" '^/etc$'
check "cat profile reads a relative path (profiles from cwd)" 'export TERM=vt100'
check "ls .. lists the parent (/ -> apps)" '^apps$'
check "find defaults to the cwd (p10hello under /apps)" 'p10hello'
check "run with a relative path (p10hello.dll)" 'Hello, Again!'
if grep -q 'no such directory' "$CLEAN"; then
    echo "  [FAIL] 'no such directory' errors present:"
    grep -n 'no such directory' "$CLEAN" | head -5
    fail=$((fail + 1))
else
    echo "  [PASS] no 'no such directory' errors"
fi

echo
if [ "$fail" -eq 0 ]; then
    echo "=== CWD CHECK: PASS ==="
else
    echo "=== CWD CHECK: FAIL ($fail) ==="
    echo "--- serial tail ---"
    tail -60 "$CLEAN"
fi
exit "$fail"
