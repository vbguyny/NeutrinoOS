#!/bin/bash
# Boot neutrinoos.img for scripts/test-vga.ps1 (WSL side).
# Usage: test-vga-boot.sh <monitor-port> <display> <wsl-build-dir>
#
# Injects the skip-boot-tests marker (and clears stale test markers), then
# starts QEMU detached with the VGA adapter, a serial log and a TCP monitor
# for sendkey / xp queries. Artifacts land in <build>/build/x64/:
#   tv-serial.log, tv-qemu.out, vga-screen.ppm (screendump target).
set -u
PORT="${1:-5599}"
DISPLAY_MODE="${2:-none}"
BUILD_DIR="${3:-/root/neutrino}"

cd "$BUILD_DIR" || { echo "build tree missing: $BUILD_DIR"; exit 1; }
pkill -9 -f qemu-system 2>/dev/null || true
sleep 1
rm -f build/x64/tv-serial.log build/x64/vga-screen.ppm
test -f build/x64/neutrinoos.img || { echo "image missing: build/x64/neutrinoos.img"; exit 1; }
test -f build/x64/OVMF_VARS.fd || cp /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS.fd
echo 1 > build/x64/tv-skip-boot-tests
mdel -i build/x64/neutrinoos.img ::/skip-boot-tests ::/console-vga-off ::/run-console-test 2>/dev/null || true
mcopy -o -i build/x64/neutrinoos.img build/x64/tv-skip-boot-tests ::/skip-boot-tests

setsid qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS.fd \
  -drive file=build/x64/neutrinoos.img,format=raw,if=virtio \
  -vga std -display "$DISPLAY_MODE" \
  -serial file:build/x64/tv-serial.log \
  -monitor tcp:127.0.0.1:"$PORT",server,nowait \
  -no-reboot -no-shutdown > build/x64/tv-qemu.out 2>&1 < /dev/null &

sleep 2
if pgrep -f qemu-system > /dev/null; then
    echo started
else
    echo "QEMU failed to start"
    exit 1
fi
