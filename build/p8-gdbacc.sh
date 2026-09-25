#!/bin/bash
# Phase 8: full npkg acceptance under GDB.
# Same step sequence as p8-npkg-test.sh, but QEMU runs with -s and GDB logs:
#   - every RhpThrowEx_Handler entry (exception object + throw-site code)
#   - any Exception.get_Message call with a bogus (small) 'this'
# The bogus call stops the guest so the script can capture the dump.
# Outputs: /root/p8acc.gdb.out (gdb), /root/p8acc.log (guest)
set -uo pipefail
exec < /dev/null
cd /root/neutrino

IMG=/root/npkgtest.img
LOG=/root/p8acc.log
FIFO=/root/p8acci
GDBCMDS=/root/p8acc.gdb
GDBOUT=/root/p8acc.gdb.out

pkill -9 -f 'qemu-system-x86_64' 2>/dev/null || true
pkill -9 -f 'gdb -batch' 2>/dev/null || true
sleep 1

rm -f "$LOG" "$FIFO" "$GDBOUT"
mkfifo "$FIFO"
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd

( tail -f "$FIFO" | timeout -s KILL 1800 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
    -drive id=bootdisk,if=none,format=raw,file="$IMG" \
    -device ide-hd,drive=bootdisk,bus=ide.0 \
    -display none -serial stdio -no-reboot -no-shutdown -s > "$LOG" 2>&1 ) &

wait_for() {
  local marker="$1" secs="$2" waited=0
  while [ "$waited" -lt "$secs" ]; do
    if grep -aq "$marker" "$LOG" 2>/dev/null; then return 0; fi
    sleep 2
    waited=$((waited + 2))
  done
  return 1
}
send() { timeout 5 bash -c "printf '%s\n' '$1' > $FIFO" || echo "WARN: fifo send failed: $1"; }
step() {
  local name="$1" cmd="$2" marker="$3" secs="$4"
  send "$cmd"
  if wait_for "$marker" "$secs"; then echo "[accg] PASS: $name"; else echo "[accg] FAIL: $name (no '$marker')"; fi
}

echo "[accg] waiting for prompt"
if ! wait_for "neutrinoos> " 150; then echo "[accg] BOOT FAILED"; tail -20 "$LOG"; exit 1; fi
echo "[accg] booted"

cat > "$GDBCMDS" <<'EOF'
set pagination off
set confirm off
target remote localhost:1234
proton-load-symbols
echo \n=== symbols loaded ===\n
break RhpThrowEx_Handler
commands
  echo \n=== RhpThrowEx_Handler ===\n
  printf "exc(rcx)=%p ctx(rdx)=%p\n", $rcx, $rdx
  printf "ctx.Rip=%p ctx.Rsp=%p ctx.Rbp=%p ctx.Rcx=%p\n", *(unsigned long*)$rdx, *(unsigned long*)($rdx+8), *(unsigned long*)($rdx+16), *(unsigned long*)($rdx+48)
  x/16i (*(unsigned long*)$rdx)-0x30
  continue
end
break kernel_ProtonOS_Runtime_ExceptionHelpers__Exception_get_Message if (unsigned long)$rcx < 0x100000
commands
  echo \n=== BOGUS get_Message (rcx small) ===\n
  info registers rax rbx rcx rdx rsi rdi rbp rsp r8 r9 r10 r11 r12 r13 r14 r15 rip
  x/1gx $rsp
  x/40gx $rsp
  set $ra = *(unsigned long*)$rsp
  x/70i $ra-0x70
  echo \n=== STOPPED: leaving guest frozen ===\n
end
echo \n=== armed ===\n
continue
EOF

( timeout -s KILL 1800 gdb -batch -x tools/gdb-protonos.py -x "$GDBCMDS" > "$GDBOUT" 2>&1 ) &
GDB_PID=$!
sleep 6
grep -aq '=== armed ===' "$GDBOUT" && echo "[accg] gdb armed"

# ---- full acceptance sequence (same as p8-npkg-test.sh) ----
step "help"            "npkg"                             "usage: npkg"                     240
step "repo-add"        "npkg repo add local /repo"       "added repository local"           90
step "list-empty"      "npkg list"                       "no packages installed"            90
step "search"          "npkg search hello"               "tests.hello-utility"              90
step "install-utility" "npkg install tests.hello-utility" "installed tests.hello-utility 1.0.0" 120
step "run-utility"     "helloutil"                        "hello-utility ok"                90
step "install-app"     "npkg install tests.hello-app"    "installed tests.hello-app 1.0.0" 120
step "run-app-wrapper" "helloapp"                         "hello-app ok"                    90
step "install-driver"  "npkg install tests.hello-driver" "installed tests.hello-driver 1.0.0" 120
step "install-chain"   "npkg install tests.chain-a"      "installed tests.chain-a 1.0.0"   180
step "list-chain"      "npkg list"                       "tests.chain-b"                   90
sleep 3
echo "[accg] sending remove (crash expected in bad runs)"
send "npkg remove tests.chain-c"

waited=0
while [ "$waited" -lt 300 ]; do
  if grep -aq 'BOGUS get_Message' "$GDBOUT" 2>/dev/null; then echo "[accg] BOGUS CALL CAUGHT"; break; fi
  if grep -aq 'RAWV' "$LOG" 2>/dev/null; then echo "[accg] guest fault dump appeared"; break; fi
  sleep 2
  waited=$((waited + 2))
done
sleep 4

echo "[accg] === gdb output ==="
sed -n '/symbols loaded/,$p' "$GDBOUT" | head -220
echo "[accg] === guest tail ==="
tail -c 800 "$LOG"

kill -9 "$GDB_PID" 2>/dev/null || true
pkill -9 -f 'qemu-system-x86_64.*npkgtes[t]' 2>/dev/null || true
pkill -9 -f 'tail -f /root/p8acci[n]' 2>/dev/null || true
echo "[accg] done"
