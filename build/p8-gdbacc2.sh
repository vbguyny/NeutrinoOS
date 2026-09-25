#!/bin/bash
# Phase 8 GDB pass 2: trace the exact corruption point of the catch funclet's
# exception object. Breakpoints:
#   RhpThrowEx (asm)         - exception at throw
#   RhpThrowEx_Handler       - exception + context at handler entry
#   0x200995fd9 (funclet)    - funclet entry rcx/rdx
#   0x200995fe8 (store)      - value stored into ex slot
#   0x20099600f (load)       - value loaded for get_Message
#   get_Message (cond small) - the bogus call (full dump, stop)
set -uo pipefail
exec < /dev/null
cd /root/neutrino

IMG=/root/npkgtest.img
LOG=/root/p8acc2.log
FIFO=/root/p8acci2
GDBCMDS=/root/p8acc2.gdb
GDBOUT=/root/p8acc2.gdb.out

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
  if wait_for "$marker" "$secs"; then echo "[accg2] PASS: $name"; else echo "[accg2] FAIL: $name (no '$marker')"; fi
}

echo "[accg2] waiting for prompt"
if ! wait_for "neutrinoos> " 150; then echo "[accg2] BOOT FAILED"; tail -20 "$LOG"; exit 1; fi
echo "[accg2] booted"

cat > "$GDBCMDS" <<'EOF'
set pagination off
set confirm off
target remote localhost:1234
proton-load-symbols
echo \n=== symbols loaded ===\n
break RhpThrowEx
commands
  echo \n=== asm RhpThrowEx ===\n
  printf "exc=%p ret=%p\n", $rcx, *(unsigned long*)$rsp
  continue
end
break RhpThrowEx_Handler
commands
  echo \n=== RhpThrowEx_Handler ===\n
  printf "exc=%p ctx=%p ctxRcx=%p\n", $rcx, $rdx, *(unsigned long*)($rdx+48)
  continue
end
break *0x200995fd9
commands
  echo \n=== FUNCLET ENTRY ===\n
  printf "rcx=%p rdx=%p\n", $rcx, $rdx
  continue
end
break *0x200995fe8
commands
  echo \n=== FUNCLET STORE ex ===\n
  printf "rax=%p rbp=%p\n", $rax, $rbp
  continue
end
break *0x20099600f
commands
  echo \n=== FUNCLET LOAD ex ===\n
  printf "slot=%p rbp=%p\n", *(unsigned long*)($rbp-936), $rbp
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
grep -aq '=== armed ===' "$GDBOUT" && echo "[accg2] gdb armed"

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
echo "[accg2] sending remove (crash expected in bad runs)"
send "npkg remove tests.chain-c"

waited=0
while [ "$waited" -lt 300 ]; do
  if grep -aq 'BOGUS get_Message' "$GDBOUT" 2>/dev/null; then echo "[accg2] BOGUS CALL CAUGHT"; break; fi
  if grep -aq 'RAWV' "$LOG" 2>/dev/null; then echo "[accg2] guest fault dump appeared"; break; fi
  sleep 2
  waited=$((waited + 2))
done
sleep 4

echo "[accg2] === gdb output ==="
sed -n '/symbols loaded/,$p' "$GDBOUT" | head -260
echo "[accg2] === guest tail ==="
tail -c 400 "$LOG"

kill -9 "$GDB_PID" 2>/dev/null || true
pkill -9 -f 'qemu-system-x86_64.*npkgtes[t]' 2>/dev/null || true
pkill -9 -f 'tail -f /root/p8acci2[n]' 2>/dev/null || true
echo "[accg2] done"
