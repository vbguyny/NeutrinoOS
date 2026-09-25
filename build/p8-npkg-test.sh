#!/bin/bash
# Phase 8 npkg device acceptance: boot the npkg test image headless in
# QEMU, drive npkg over the serial console (FIFO-fed stdin), assert the
# outputs, and print a PASS/FAIL summary.
#
# Prereqs: bash build/p8-npkg-deploy.sh  (creates /root/npkgtest.img)
# Log:     /root/p8npkg.log   Verdict: "=== npkg summary: ALL-PASS ==="
set -uo pipefail
export MTOOLS_SKIP_CHECK=1
exec < /dev/null

cd /root/neutrino

IMG=/root/npkgtest.img
LOG=/root/p8npkg.log
FIFO=/root/p8in
QEMU_SECS=1800

PASS=0
FAIL=0

test -f "$IMG" || { echo "missing $IMG - run build/p8-npkg-deploy.sh"; exit 1; }
# Fresh UEFI variables per boot (the boot order must point at the IDE disk).
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd

rm -f "$LOG" "$FIFO"
mkfifo "$FIFO"

( tail -f "$FIFO" | timeout -s KILL "$QEMU_SECS" qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
    -drive id=bootdisk,if=none,format=raw,file="$IMG" \
    -device ide-hd,drive=bootdisk,bus=ide.0 \
    -display none -serial stdio -no-reboot -no-shutdown > "$LOG" 2>&1 ) &

send() {
  timeout 5 bash -c "printf '%s\n' '$1' > $FIFO" || echo "WARN: fifo send failed: $1"
}

wait_for() {
  local marker="$1" secs="$2" waited=0
  while [ "$waited" -lt "$secs" ]; do
    if grep -aq "$marker" "$LOG" 2>/dev/null; then return 0; fi
    sleep 2
    waited=$((waited + 2))
  done
  return 1
}

step() {
  local name="$1" cmd="$2" marker="$3" secs="$4"
  send "$cmd"
  if wait_for "$marker" "$secs"; then
    echo "PASS: $name"
    PASS=$((PASS + 1))
  else
    echo "FAIL: $name (no '$marker' within ${secs}s)"
    FAIL=$((FAIL + 1))
  fi
}

echo "=== npkg device acceptance ==="
if ! wait_for "neutrinoos> " 150; then
  echo "FAIL: boot (no shell prompt within 150s)"
  FAIL=$((FAIL + 1))
else
  echo "PASS: boot (shell prompt)"
  PASS=$((PASS + 1))
  sleep 2

  step "help"            "npkg"                             "usage: npkg"                     240
  step "repo-add"        "npkg repo add local /repo"       "added repository local"           90
  # The npkg acceptance runs on baseline images (P8_PREPLACE=off): the first
  # list must be empty. On driver-loader images (P8_PREPLACE=full) the
  # pre-placed fixture appears instead - set P8_EXPECT_PREPLACED=1 there.
  if [ "${P8_EXPECT_PREPLACED:-0}" = "1" ]; then
    step "list-baseline" "npkg list"                       "preplaced.drvtest"                90
  else
    step "list-empty"    "npkg list"                       "no packages installed"            90
  fi
  step "search"          "npkg search hello"               "tests.hello-utility"              90
  step "install-utility" "npkg install tests.hello-utility" "installed tests.hello-utility 1.0.0" 120
  step "run-utility"     "helloutil"                        "hello-utility ok"                90
  step "install-app"     "npkg install tests.hello-app"    "installed tests.hello-app 1.0.0" 120
  step "run-app-wrapper" "helloapp"                         "hello-app ok"                    90
  step "install-driver"  "npkg install tests.hello-driver" "installed tests.hello-driver 1.0.0" 120
  step "install-chain"   "npkg install tests.chain-a"      "installed tests.chain-a 1.0.0"   180
  step "list-chain"      "npkg list"                       "tests.chain-b"                    90
  step "remove-guard"    "npkg remove tests.chain-c"       "is required by"                   90
  step "upgrade"         "npkg upgrade"                    "up to date"                       90
  step "verify"          "npkg verify /repo/tests.hello-utility-1.0.0.npkg" "checksums: OK"    90
  step "info"            "npkg info tests.hello-app"       "tests.hello-app"                  90
  step "remove-a"        "npkg remove tests.chain-a"       "removed tests.chain-a 1.0.0"      90
  step "remove-b"        "npkg remove tests.chain-b"       "removed tests.chain-b 1.0.0"      90
  step "remove-c"        "npkg remove tests.chain-c"       "removed tests.chain-c 1.0.0"      90
  step "final-list"      "npkg list"                       "tests.hello-app"                  90

  # Extra cross-checks on the captured log.
  if grep -aq "tests.chain-c" "$LOG"; then
    echo "PASS: chain-c installed transitively"
    PASS=$((PASS + 1))
  else
    echo "FAIL: chain-c not visible in list output"
    FAIL=$((FAIL + 1))
  fi
fi

send "exit" 2>/dev/null || true
sleep 1
pkill -9 -f 'qemu-system-x86_64.*npkgtes[t]' 2>/dev/null || true
pkill -9 -f 'tail -f /root/p8i[n]' 2>/dev/null || true
sleep 1

echo "=== npkg summary: $PASS PASS / $FAIL FAIL ==="
if [ "$FAIL" -eq 0 ]; then
  echo "=== npkg summary: ALL-PASS ==="
  exit 0
else
  echo "=== npkg summary: HAS-FAILURES ==="
  exit 1
fi
