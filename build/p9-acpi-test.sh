#!/bin/bash
# Phase 9 ACPI power management acceptance (Task 4):
#
#   1. poweroff: boot QEMU q35, run the `poweroff` builtin; assert the
#      kernel reports the ACPI S5 mechanism (PM1_CNT + \_S5 SLP_TYP) and
#      that QEMU exits by itself (S5 shuts the machine down; no
#      -no-shutdown flag here on purpose).
#   2. reboot: boot again, run the `reboot` builtin; assert the machine
#      resets and reaches a second shell prompt in the same QEMU process.
#   3. sleep (S3): boot with a QEMU monitor socket, run the `cpupower`
#      builtin, then the `sleep` builtin; assert the kernel reports S3
#      entry with the \_S3 SLP_TYP values, that QEMU reports the VM run
#      state as paused (suspended), and that a monitor `system_wakeup`
#      flips it back to running. Guest-side continuation after the wake
#      is firmware/SMM-dependent on QEMU and is documented as a platform
#      limitation (docs/PHASE9-ACPI.md); VirtualBox and real hardware are
#      the guest-resume verification targets. On QEMU q35 the firmware
#      declares no _PTS/_WAK methods; the kernel reports that.
#   4. cpupower: in the same (resumed) VM, run the `cpupower` builtin;
#      assert the C-state/P-state report lines appear.
#
# The standard image (build/x64/neutrinoos.img) is used: power management
# must work with the boot test suites enabled. The AML interpreter
# itself is covered by the [AML] boot self-test (synthetic _PTS/_WAK-
# shaped methods, since QEMU firmware has none).
#
# Logs: /root/p9acpi-poweroff.log, /root/p9acpi-reboot.log, /root/p9acpi-sleep.log
# Verdict: "=== acpi summary: ALL-PASS ==="
set -uo pipefail
export MTOOLS_SKIP_CHECK=1

IMG=/root/neutrino/build/x64/neutrinoos.img
VARS=/tmp/p9acpi-vars.fd
POWER_LOG=/root/p9acpi-poweroff.log
REBOOT_LOG=/root/p9acpi-reboot.log
SLEEP_LOG=/root/p9acpi-sleep.log
POWER_SER=/tmp/p9acpi1
REBOOT_SER=/tmp/p9acpi2
SLEEP_SER=/tmp/p9acpi3
SLEEP_SOCK=/tmp/p9acpi3.sock

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
  pkill -9 -f 'p9acpi[123]' 2>/dev/null || true
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
sleep 1

# ---- 3. sleep (S3) + 4. cpupower ----------------------------------------
make_serial_pipes "$SLEEP_SER"
rm -f "$SLEEP_SOCK" "$SLEEP_LOG"
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd "$VARS"
qemu-system-x86_64 -name p9acpi-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file="$VARS" \
  -drive id=bootdisk,if=none,format=raw,file="$IMG" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial pipe:"$SLEEP_SER" \
  -monitor unix:"$SLEEP_SOCK",server,nowait > /dev/null 2>&1 &
QPID=$!
cat "$SLEEP_SER.out" > "$SLEEP_LOG" &
CATPID=$!

wait_for_file "$SLEEP_LOG" "neutrinoos> " 240
result "sleep test boot (shell prompt)" $?

# 4. cpupower first (before the suspend changes the run state).
printf 'cpupower\r' > "$SLEEP_SER.in"
wait_for_file "$SLEEP_LOG" "\\[cpupower\\] CPU: " 30
result "cpupower reported CPU capabilities" $?
grep -aq "\\[cpupower\\] C-states:" "$SLEEP_LOG"
result "cpupower reported C-states" $?
grep -aq "\\[cpupower\\] P-states:" "$SLEEP_LOG"
result "cpupower reported P-states" $?

printf 'sleep\r' > "$SLEEP_SER.in"
wait_for_file "$SLEEP_LOG" "\\[power\\] ACPI enter S3" 30
result "sleep entered S3 (PM1_CNT write)" $?
grep -aq '(\\_S3)' "$SLEEP_LOG"
result "S3 SLP_TYP values evaluated from \\_S3 AML" $?
grep -aq "no _PTS method" "$SLEEP_LOG"
result "no _PTS method reported (QEMU firmware)" $?

# The VM must be suspended now (QEMU run state), and QEMU still alive.
mon_status() {   # print the monitor's "info status" reply
python3 - "$SLEEP_SOCK" <<'PY'
import socket, sys, time
s = socket.socket(socket.AF_UNIX)
s.settimeout(3.0)
try:
    s.connect(sys.argv[1])
except Exception as e:
    print("MON-ERROR", e)
    sys.exit(0)
time.sleep(0.3)
try: s.recv(4096)
except Exception: pass
s.sendall(b'info status\n')
time.sleep(0.8)
try:
    print(s.recv(4096).decode(errors='replace'))
except Exception as e:
    print("MON-ERROR", e)
s.close()
PY
}

sleep 3
mon_status | grep -aq 'paused (suspended)'
result "QEMU reports the VM suspended (S3)" $?

# Wake the VM through the QEMU monitor. NOTE: on QEMU x86 the wake path
# runs through firmware SMM (an SMI is raised on wakeup). Without an OS
# S3-resume setup (FACS waking vector + firmware S3 boot script) the
# *guest* does not continue execution after the wake; the VM-level wake
# itself is verified here. VirtualBox and real hardware are the
# platforms where the guest-side resume is exercised (documented in
# docs/PHASE9-ACPI.md).
python3 - "$SLEEP_SOCK" <<'PY'
import socket, sys, time
s = socket.socket(socket.AF_UNIX)
s.settimeout(3.0)
s.connect(sys.argv[1])
time.sleep(0.5)
s.sendall(b'system_wakeup\n')
time.sleep(1.0)
s.close()
PY
sleep 2
mon_status | grep -aq 'running'
result "monitor system_wakeup resumed the VM (QEMU run state)" $?

ALIVE=1
kill -0 "$QPID" 2>/dev/null && ALIVE=0
result "QEMU process alive after wake" $ALIVE

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
echo "--- sleep log tail ---"
tail -12 "$SLEEP_LOG" 2>/dev/null || true
exit 1
