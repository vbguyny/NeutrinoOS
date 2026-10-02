#!/bin/bash
# Virtio boot-volume check: boots the image with the boot disk attached as
# virtio-blk and verifies that boot-volume file access works there
# (Platform.BootStorage -> VirtioBlkEntry FAT helpers):
#   - cd /apps      -> prompt "root-/apps> " (Directory.Exists via the bridge)
#   - cd /drivers   -> prompt "root-/drivers> "
#   - pwd           -> "/drivers"
#   - cd /          -> prompt "root-/> "
# plus, on an image that carries /bin utilities (the CLI image):
#   - ls /apps             (ListBootDirEntry)
#   - cat /etc/boot.params (FileBootRead)
#
# Usage: bash build/virtio-boot-check.sh [image-path] [legacy|modern]
# (defaults: build/x64/neutrinoos.img, modern). The image is copied to
# /tmp first so the skip-boot-tests marker can be added safely.
set -u
IMG="${1:-build/x64/neutrinoos.img}"
MODE="${2:-modern}"

cd /root/neutrino || exit 1
pkill -9 -f qin-virt 2>/dev/null || true
pkill -9 -f qemu-system 2>/dev/null || true
sleep 1

rm -f /root/qin-virt /root/virt-qemu.out
cp -f "$IMG" /tmp/virt-check.img || { echo "image missing: $IMG"; exit 1; }
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-virt.fd
echo 1 > /tmp/virt-skip-marker
mdel -i /tmp/virt-check.img ::/skip-boot-tests ::/run-console-test 2>/dev/null || true
mcopy -o -i /tmp/virt-check.img /tmp/virt-skip-marker ::/skip-boot-tests

if [ "$MODE" = "legacy" ]; then
    VIRTIO_DEV="virtio-blk-pci,drive=virtdisk"
else
    VIRTIO_DEV="virtio-blk-pci,drive=virtdisk,disable-legacy=on"
fi

# Fresh OVMF VARS per run (a stale NVRAM can fall through to PXE).
mkfifo /root/qin-virt
setsid bash -c "tail -f /root/qin-virt | qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-virt.fd \
  -drive id=virtdisk,if=none,format=raw,file=/tmp/virt-check.img \
  -device $VIRTIO_DEV \
  -vga std -display none -serial stdio \
  -no-reboot -no-shutdown" < /dev/null > /root/virt-qemu.out 2>&1 &

LOG=/root/virt-qemu.out
echo "[VIRTIO] waiting for the shell prompt..."
for i in $(seq 1 120); do
    if grep -q 'root-/>' "$LOG" 2>/dev/null; then break; fi
    sleep 1
done

send() { printf '%s\r' "$1" > /root/qin-virt; sleep 2; }

send 'cd /apps'
send 'cd /drivers'
send 'pwd'
send 'cd /'

# CLI image extras (has /bin utilities): ls + cat file reads.
HAS_BIN=$(mdir -i /tmp/virt-check.img ::/bin 2>/dev/null | head -1)
if [ -n "$HAS_BIN" ]; then
    send 'ls /apps'
    send 'cat /etc/profile'
    send 'run /apps/p10hello.dll'
    send 'NeutrinoVirt'
    sleep 4
fi

sleep 2
pkill -9 -f qin-virt 2>/dev/null || true
pkill -9 -f qemu-system 2>/dev/null || true
sleep 1

fail=0
CLEAN=/root/virt-qemu-clean.log
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
echo "[VIRTIO] results (image: $IMG, mode: $MODE)"
check "prompt is {user}-{pwd}> (root-/>)" 'root-/>'
check "cd /apps tracks the prompt (root-/apps>)" 'root-/apps>'
check "cd /drivers tracks the prompt (root-/drivers>)" 'root-/drivers>'
check "pwd prints /drivers (file I/O, not just cwd state)" '^/drivers$'
if grep -q 'no such directory' "$CLEAN"; then
    echo "  [FAIL] 'no such directory' errors present:"
    grep -n 'no such directory' "$CLEAN" | head -5
    fail=$((fail + 1))
else
    echo "  [PASS] no 'no such directory' errors"
fi
if [ -n "$HAS_BIN" ]; then
    check "ls /apps lists p10hello.dll (ListBootDirEntry)" 'p10hello'
    check "cat /etc/profile printed file content (FileBootRead)" 'export TERM=vt100'
    check "run /apps/p10hello.dll works (AssemblyRunner + JIT)" 'Hello, NeutrinoVirt!'
fi

echo
if [ "$fail" -eq 0 ]; then
    echo "=== VIRTIO BOOT CHECK: PASS ==="
else
    echo "=== VIRTIO BOOT CHECK: FAIL ($fail) ==="
    echo "--- serial tail ---"
    tail -40 "$CLEAN"
fi
exit "$fail"
