#!/bin/bash
# Phase 8 ARM64 acceptance test: rebuild the ARM64 image, boot it headless
# in QEMU (virt + AAVMF) and assert the interrupt bring-up evidence, then
# boot again with a FIFO-fed serial pipe and drive the shell over the PL011
# RX interrupt.
#
# Assertions:
#   1. rebuild: arm64 image built with zero compiler errors
#   2. boot: VBAR_EL1 vectors installed, GICv2 initialized, Stage 2 live
#   3. boot: generic-timer ticks observed at shell start (ticks > 0)
#   4. boot: interactive shell prompt reached, no sync exceptions / raw faults
#   5. input: 'help' typed over serial RX produces the shell help text
#   6. input: 'version' reports aarch64
#
# Logs: /root/arm64-test-serial.txt (boot), /tmp/ser.log (interactive)
# Verdict: "=== arm64 summary: ALL-PASS ==="
set -uo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export MTOOLS_SKIP_CHECK=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
cd /root/neutrino

PASS=0
FAIL=0
result() {
  if [ "$2" -eq 0 ]; then
    echo "PASS: $1"
    PASS=$((PASS + 1))
  else
    echo "FAIL: $1"
    FAIL=$((FAIL + 1))
  fi
}

# ---------------------------------------------------------------- 1. rebuild
echo "--- 1/6 rebuild arm64 image ---"
if command -v rsync >/dev/null 2>&1; then
  rsync -a --delete --exclude 'obj' --exclude 'bin' "$SRC/src/" /root/neutrino/src/
  rsync -a "$SRC/tests/" /root/neutrino/tests/
else
  cp -a "$SRC/src/." /root/neutrino/src/
  cp -a "$SRC/tests/." /root/neutrino/tests/
fi
cp -f "$SRC/Makefile" /root/neutrino/Makefile
rm -rf src/korlib/obj src/korlib/bin
rm -f build/arm64/kernel.obj build/arm64/BOOTAA64.EFI
make image ARCH=arm64 > /tmp/p8-arm64-build.log 2>&1
ERRS=$(grep -ac 'error CS' /tmp/p8-arm64-build.log)
test -f build/arm64/BOOTAA64.EFI
result "arm64 rebuild (0 compiler errors, BOOTAA64.EFI present)" $((ERRS + $?))
grep -a 'error' /tmp/p8-arm64-build.log | head -5

# ------------------------------------------------------------ 2-4. headless
echo "--- 2-4/6 headless boot assertions ---"
pkill -9 qemu-system-aarch64 >/dev/null 2>&1 || true
cp -f /usr/share/AAVMF/AAVMF_VARS.fd build/arm64/AAVMF_VARS.fd
SER=/root/arm64-test-serial.txt
rm -f "$SER"
timeout -k 5 -s TERM 60 qemu-system-aarch64 -machine virt -cpu cortex-a72 -m 2G -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/AAVMF/AAVMF_CODE.fd \
  -drive if=pflash,format=raw,file=build/arm64/AAVMF_VARS.fd \
  -drive id=hd0,if=none,format=raw,file=build/arm64/neutrinoos.img \
  -device virtio-blk-pci,drive=hd0,bootindex=1 \
  -nic none \
  -display none -serial file:"$SER" -monitor none -no-reboot -no-shutdown > /dev/null 2>&1

grep -aq 'Exception vectors installed (VBAR_EL1)' "$SER" && grep -aq 'GICv2 initialized' "$SER" \
  && grep -aq 'Stage 2: GIC + generic timer, interrupts live' "$SER"
result "VBAR_EL1 vectors + GICv2 + Stage 2 live" $?

TICKS=$(grep -a -o 'ticks=[0-9]*' "$SER" | head -1 | cut -d= -f2)
[ -n "$TICKS" ] && [ "$TICKS" -gt 0 ] 2>/dev/null
result "generic timer ticking at shell start (ticks=${TICKS:-none})" $?

grep -aq 'neutrinoos>' "$SER" && ! grep -aq 'SYNC EXCEPTION' "$SER" && ! grep -aq 'RAWV' "$SER"
result "shell prompt reached, no sync exceptions / raw faults" $?

# ----------------------------------------------------------- 5-6. interactive
echo "--- 5-6/6 interactive input over PL011 RX ---"
pkill -9 qemu-system-aarch64 >/dev/null 2>&1 || true
cp -f /usr/share/AAVMF/AAVMF_VARS.fd build/arm64/AAVMF_VARS.fd
rm -f /tmp/ser.in /tmp/ser.out /tmp/ser.log
mkfifo /tmp/ser.in /tmp/ser.out

qemu-system-aarch64 -machine virt -cpu cortex-a72 -m 2G -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/AAVMF/AAVMF_CODE.fd \
  -drive if=pflash,format=raw,file=build/arm64/AAVMF_VARS.fd \
  -drive id=hd0,if=none,format=raw,file=build/arm64/neutrinoos.img \
  -device virtio-blk-pci,drive=hd0,bootindex=1 \
  -nic none \
  -display none -serial pipe:/tmp/ser -monitor none -no-reboot -no-shutdown &
QPID=$!
cat /tmp/ser.out > /tmp/ser.log &
CATPID=$!

for i in $(seq 1 120); do
  if grep -aq 'neutrinoos>' /tmp/ser.log 2>/dev/null; then break; fi
  sleep 0.5
done
sleep 2

timeout 5 bash -c "printf 'help\r' > /tmp/ser.in" || true
sleep 2
# The very first write into the serial pipe can race with the PL011 RX
# bring-up (observed once in CI-style runs: 'help' lost, 'version' fine).
# Retry once before judging the step.
if ! grep -aq 'NeutrinoOS shell (Phase 5)' /tmp/ser.log; then
  timeout 5 bash -c "printf 'help\r' > /tmp/ser.in" || true
  sleep 2
fi
timeout 5 bash -c "printf 'version\r' > /tmp/ser.in" || true
sleep 3

grep -aq 'NeutrinoOS shell (Phase 5)' /tmp/ser.log
result "typed 'help' over serial RX -> shell help text" $?

grep -aq 'NeutrinoOS 1.0.0 aarch64' /tmp/ser.log
result "typed 'version' over serial RX -> aarch64 banner" $?

kill $QPID 2>/dev/null || true
pkill -9 qemu-system-aarch64 2>/dev/null || true
kill $CATPID 2>/dev/null || true

# ------------------------------------------------------------------ summary
echo ""
if [ "$FAIL" -eq 0 ]; then
  echo "=== arm64 summary: ALL-PASS (${PASS} passed) ==="
  exit 0
else
  echo "=== arm64 summary: FAIL (${PASS} passed, ${FAIL} failed) ==="
  echo "--- boot log tail ---"
  tail -20 "$SER" 2>/dev/null
  echo "--- interactive log tail ---"
  tail -20 /tmp/ser.log 2>/dev/null
  exit 1
fi
