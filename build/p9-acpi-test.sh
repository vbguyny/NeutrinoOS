#!/bin/bash
# Phase 9 ACPI power management acceptance (Task 4 milestone 1):
#
#   1. poweroff: boot QEMU q35, run the `poweroff` builtin; assert the
#      kernel reports the ACPI S5 mechanism (PM1_CNT + \_S5 SLP_TYP) and
#      that QEMU exits by itself (S5 shuts the machine down; no
#      -no-shutdown flag here on purpose).
#   2. reboot: boot again, run the `reboot` builtin; assert the machine
#      resets and reaches a second shell prompt in the same QEMU process.
#
# The standard image (build/x64/neutrinoos.img) is used: power management
# must work with the boot test suites enabled.
#
# Logs: /root/p9acpi-poweroff.log, /root/p9acpi-reboot.log
# Verdict: "=== acpi summary: ALL-PASS ==="
set -uo pipefail
export MTOOLS_SKIP_CHECK=1

IMG=/root/neutrino/build/x64/neutrinoos.img
VARS=/tmp/p9acpi-vars.fd
POWER_LOG=/root/p9acpi-poweroff.log
REBOOT_LOG=/root/p9acpi-reboot.log
POWER_SER=/tmp/p9acpi1
REBOOT_SER=/tmp/p9acpi2

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

cleanup_qemu() {
  pkill -9 -f 'p9acpi-qemu' 2>/dev/null || true
  pkill -9 -f 'p9acpi[12]' 2>/dev/null || true
}

wait_for_file() {   # file marker secs
  local f="$1" marker="$2" secs="$3" waited=0
  while [ "$waited" -lt "$secs" ]; do
    grep -aq "$marker" "$f" 2>/dev/null && return 0
    sleep 1
    waited=$((waited + 1))
  done
  return 1
}

# QEMU's `-serial pipe:` connects to pre-existing FIFOs; create them.
make_serial_pipes() {   # base name
  rm -f "$1.in" "$1.out"
  mkfifo "$1.in" "$1.out"
}

echo "=== ACPI power management acceptance ==="
test -f "$IMG" || { echo "missing $IMG - run make image first"; exit 1; }
cleanup_qemu
rm -f "$POWER_LOG" "$REBOOT_LOG"

# ---- 1. poweroff (S5) ---------------------------------------------------
make_serial_pipes "$POWER_SER"
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd "$VARS"
qemu-system-x86_64 -name p9acpi-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file="$VARS" \
  -drive id=bootdisk,if=none,format=raw,file="$IMG" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial pipe:"$POWER_SER" -no-reboot > /dev/null 2>&1 &
QPID=$!
cat "$POWER_SER.out" > "$POWER_LOG" &
CATPID=$!

wait_for_file "$POWER_LOG" "neutrinoos> " 240
result "boot (shell prompt)" $?
wait_for_file "$POWER_LOG" "\[power\] ACPI: " 10
result "ACPI power management detected at first use" $?

printf 'poweroff\r' > "$POWER_SER.in"
wait_for_file "$POWER_LOG" "\[power\] ACPI S5 power off" 30
result "poweroff triggered ACPI S5 (PM1_CNT write)" $?
grep -aq '(\\_S5)' "$POWER_LOG"
result "SLP_TYP values evaluated from \\_S5 AML" $?

# QEMU must exit by itself now (S5 shut the machine down).
EXITED=1
for i in $(seq 1 60); do
  if ! kill -0 "$QPID" 2>/dev/null; then EXITED=0; break; fi
  sleep 1
done
result "QEMU exited on S5 (machine powered off)" $EXITED
kill -9 "$QPID" "$CATPID" 2>/dev/null || true
cleanup_qemu
sleep 1

# ---- 2. reboot (reset) --------------------------------------------------
make_serial_pipes "$REBOOT_SER"
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd "$VARS"
qemu-system-x86_64 -name p9acpi-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file="$VARS" \
  -drive id=bootdisk,if=none,format=raw,file="$IMG" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial pipe:"$REBOOT_SER" > /dev/null 2>&1 &
QPID=$!
cat "$REBOOT_SER.out" > "$REBOOT_LOG" &
CATPID=$!

wait_for_file "$REBOOT_LOG" "neutrinoos> " 240
result "reboot test boot (shell prompt)" $?

printf 'reboot\r' > "$REBOOT_SER.in"
wait_for_file "$REBOOT_LOG" "\[power\] ACPI reset" 30
result "reboot triggered the reset path" $?

# The machine resets and boots again: a second shell-ready banner must
# appear in the same QEMU process.
BOOTS=0
for i in $(seq 1 150); do
  BOOTS=$(grep -ac 'NeutrinoOS console ready' "$REBOOT_LOG" 2>/dev/null || true)
  [ "${BOOTS:-0}" -ge 2 ] && break
  sleep 1
done
[ "${BOOTS:-0}" -ge 2 ]
result "machine rebooted (second shell banner, boots=$BOOTS)" $?

kill -9 "$QPID" "$CATPID" 2>/dev/null || true
cleanup_qemu

echo "=== acpi summary: $PASS PASS / $FAIL FAIL ==="
if [ "$FAIL" -eq 0 ]; then
  echo "=== acpi summary: ALL-PASS ==="
  exit 0
fi
echo "=== acpi summary: HAS-FAILURES ==="
echo "--- poweroff log tail ---"
tail -12 "$POWER_LOG" 2>/dev/null || true
echo "--- reboot log tail ---"
tail -12 "$REBOOT_LOG" 2>/dev/null || true
exit 1
