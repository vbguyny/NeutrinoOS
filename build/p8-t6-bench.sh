#!/bin/bash
# Phase 8 Task 6 benchmarks (spec: npkg install time for a package with 10
# dependencies; driver load time for a hot-plugged VirtIO device; ARM64
# boot time compared to x86-64).
#
#   - npkg install tests.bench-root (10 dependencies) on the npkg test
#     image: wall time from command send to "installed tests.bench-root".
#     Run build/p8-npkg-tests-build.sh + build/p8-npkg-deploy.sh first.
#   - x64 boot: QEMU launch -> "neutrinoos> " on the standard image.
#   - arm64 boot: same method on the standard arm64 image (boot both from
#     plain `make image` products so the comparison is like-for-like).
#   - Hot-plug driver load/unload times are reported by
#     build/p8-hotplug-test.sh (334 ms / 329 ms on this machine).
#
# Results: "[bench] <name>: <value>" lines + "=== bench summary ===".
set -uo pipefail
export MTOOLS_SKIP_CHECK=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
cd /root/neutrino

FIFO=/tmp/p8bench.fifo
X64_SER=/root/p8bench-x64.ser
A64_SER=/root/p8bench-arm64.ser
ARM64_IMG=build/arm64/neutrinoos.img
X64_IMG=build/x64/neutrinoos.img
NPKG_IMG=/root/npkgtest.img

wait_for() {   # file marker secs
  local f="$1" marker="$2" secs="$3" waited=0
  while [ "$waited" -lt "$secs" ]; do
    grep -aq "$marker" "$f" 2>/dev/null && return 0
    sleep 1
    waited=$((waited + 1))
  done
  return 1
}

cleanup_qemu() {
  pkill -9 -f 'p8bench-qemu' 2>/dev/null || true
  pkill -9 -f 'p8bench.fif[o]' 2>/dev/null || true
}

echo "=== phase 8 benchmarks ==="
cleanup_qemu
rm -f "$FIFO" "$X64_SER" "$A64_SER"

# ---- 1. x64 boot time (standard image) ---------------------------------
t0=$(date +%s.%N)
mkfifo "$FIFO"
( tail -f "$FIFO" | timeout -s KILL 600 qemu-system-x86_64 -name p8bench-qemu \
    -machine q35 -m 2G -cpu max -smp 1 \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
    -drive id=bootdisk,if=none,format=raw,file="$X64_IMG" \
    -device ide-hd,drive=bootdisk,bus=ide.0 \
    -display none -serial stdio -no-reboot -no-shutdown > "$X64_SER" 2>&1 ) &
if wait_for "$X64_SER" "neutrinoos> " 300; then
  t1=$(date +%s.%N)
  X64MS=$(awk -v a="$t0" -v b="$t1" 'BEGIN { printf "%.0f", (b-a)*1000 }')
  echo "[bench] x64 boot to shell: ${X64MS} ms"
else
  X64MS=unknown
  echo "[bench] x64 boot: FAILED (no prompt within 300s)"
fi
cleanup_qemu
sleep 2

# ---- 2. arm64 boot time (standard image) -------------------------------
if [ -f "$ARM64_IMG" ]; then
  cp -f /usr/share/AAVMF/AAVMF_VARS.fd build/arm64/AAVMF_VARS.fd
  t0=$(date +%s.%N)
  timeout -s KILL 600 qemu-system-aarch64 -name p8bench-qemu \
    -machine virt -cpu cortex-a72 -m 2G -smp 1 \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/AAVMF/AAVMF_CODE.fd \
    -drive if=pflash,format=raw,file=build/arm64/AAVMF_VARS.fd \
    -drive id=hd0,if=none,format=raw,file="$ARM64_IMG" \
    -device virtio-blk-pci,drive=hd0,bootindex=1 \
    -nic none \
    -display none -serial file:"$A64_SER" -monitor none -no-reboot -no-shutdown > /dev/null 2>&1 &
  if wait_for "$A64_SER" "neutrinoos> " 300; then
    t1=$(date +%s.%N)
    A64MS=$(awk -v a="$t0" -v b="$t1" 'BEGIN { printf "%.0f", (b-a)*1000 }')
    echo "[bench] arm64 boot to shell: ${A64MS} ms"
  else
    A64MS=unknown
    echo "[bench] arm64 boot: FAILED (no prompt within 300s)"
  fi
  cleanup_qemu
  sleep 2
else
  echo "[bench] arm64 boot: SKIPPED (build first: make image ARCH=arm64)"
fi

# ---- 3. npkg install with ten dependencies -----------------------------
if [ -f "$NPKG_IMG" ] && grep -aq "tests.bench-root" /root/p8repo/repository.json 2>/dev/null; then
  t0=$(date +%s.%N)
  ( tail -f "$FIFO" | timeout -s KILL 600 qemu-system-x86_64 -name p8bench-qemu \
      -machine q35 -m 2G -cpu max -smp 1 \
      -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
      -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
      -drive id=bootdisk,if=none,format=raw,file="$NPKG_IMG" \
      -device ide-hd,drive=bootdisk,bus=ide.0 \
      -display none -serial stdio -no-reboot -no-shutdown > "$X64_SER" 2>&1 ) &
  if wait_for "$X64_SER" "neutrinoos> " 300; then
    tboot=$(date +%s.%N)
    # The deploy rebuilds the image from the base image, so the test
    # repository is not configured yet: add it like the acceptance suite.
    timeout 5 bash -c "printf '%s\n' 'npkg repo add local /repo' > $FIFO"
    if ! wait_for "$X64_SER" "added repository local" 60; then
      echo "[bench] npkg install: FAILED (repo add did not confirm)"
    fi
    timeout 5 bash -c "printf '%s\n' 'npkg install tests.bench-root' > $FIFO"
    t0=$(date +%s.%N)
    if wait_for "$X64_SER" "installed tests.bench-root 1.0.0" 240; then
      t1=$(date +%s.%N)
      INSTMS=$(awk -v a="$t0" -v b="$t1" 'BEGIN { printf "%.0f", (b-a)*1000 }')
      echo "[bench] npkg install (tests.bench-root, 10 deps): ${INSTMS} ms"
    else
      echo "[bench] npkg install: FAILED (no install confirmation within 240s)"
    fi
  else
    echo "[bench] npkg install: FAILED (no shell prompt within 300s)"
  fi
  cleanup_qemu
else
  echo "[bench] npkg install: SKIPPED (run p8-npkg-tests-build.sh + p8-npkg-deploy.sh first)"
fi

rm -f "$FIFO"
echo "=== bench summary: x64=${X64MS}ms arm64=${A64MS:-n/a}ms ==="
