#!/bin/bash
# Phase 9 USB acceptance (Task 1): QEMU q35 with qemu-xhci, a USB
# keyboard and a USB mass storage stick.
#
#   1. boot:      xHCI driver starts, keyboard and storage enumerate
#   2. usb cmd:   devices listed (HID keyboard + disk), disk ready
#   3. keyboard:  monitor sendkey types "version" through the USB HID
#                 stack -> the shell runs it (end-to-end input proof)
#   4. hot-plug:  monitor device_add usb-kbd brings up a second keyboard;
#                 device_del removes it (driver unbind + slot disable)
#
# Logs: /root/p9usb.log
# Verdict: "=== usb summary: ALL-PASS ==="
set -uo pipefail
export MTOOLS_SKIP_CHECK=1

IMG=/root/neutrino/build/x64/neutrinoos.img
VARS=/tmp/p9usb-vars.fd
LOG=/root/p9usb.log
SER=/tmp/p9usb.ser
SOCK=/tmp/p9usb.sock
STICK=/root/p9usb-stick.img

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
  pkill -9 -f 'p9usb-qem[u]' 2>/dev/null || true
  pkill -9 -f 'p9usb[.]ser' 2>/dev/null || true
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

mon() {   # send one monitor command
python3 - "$SOCK" "$1" <<'PY'
import socket, sys, time
s = socket.socket(socket.AF_UNIX)
s.settimeout(3.0)
try:
    s.connect(sys.argv[1])
except Exception as e:
    print("MON-ERROR", e)
    sys.exit(0)
time.sleep(0.3)
try: s.recv(8192)
except Exception: pass
s.sendall(sys.argv[2].encode() + b'\n')
time.sleep(0.5)
try: s.recv(8192)
except Exception: pass
s.close()
PY
}

echo "=== USB stack acceptance ==="
test -f "$IMG" || { echo "missing $IMG - run make image first"; exit 1; }
cleanup_qemu
rm -f "$LOG" "$SOCK" /tmp/p9usb.ser.in /tmp/p9usb.ser.out

# USB stick (16 MB FAT16 with a README) unless it exists.
if [ ! -f "$STICK" ]; then
  dd if=/dev/zero of="$STICK" bs=1M count=16 status=none
  mformat -i "$STICK" -F -v NEUTRINOUS >/dev/null 2>&1 || true
  printf 'NeutrinoOS USB mass storage test volume.\r\n' > /tmp/p9usb-readme.txt
  mcopy -i "$STICK" /tmp/p9usb-readme.txt ::/README.TXT >/dev/null 2>&1 || true
fi

mkfifo /tmp/p9usb.ser.in /tmp/p9usb.ser.out
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd "$VARS"

qemu-system-x86_64 -name p9usb-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file="$VARS" \
  -drive id=bootdisk,if=none,format=raw,file="$IMG" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -device qemu-xhci \
  -device usb-kbd \
  -drive id=usbstick,if=none,format=raw,file="$STICK" \
  -device usb-storage,drive=usbstick \
  -display none -serial pipe:/tmp/p9usb.ser \
  -monitor unix:"$SOCK",server,nowait > /dev/null 2>&1 &
QPID=$!
cat /tmp/p9usb.ser.out > "$LOG" &
CATPID=$!

wait_for_file "$LOG" "neutrinoos> " 240
result "boot (shell prompt)" $?

grep -aq '\[usb-xhci\] xHCI running' "$LOG"
result "xHCI controller started" $?
grep -aq 'HID keyboard bound' "$LOG"
result "USB keyboard enumerated (HID)" $?
grep -aq 'disk sda' "$LOG"
result "USB mass storage enumerated (BOT)" $?

# usb command output.
printf 'usb\r' > /tmp/p9usb.ser.in
wait_for_file "$LOG" "\[usb\] xHCI: ports=" 30
result "usb command reports the controller" $?
grep -aq '\[usb\] dev0' "$LOG"
result "usb command lists devices" $?
grep -aq '\[usb\] disk sda: ' "$LOG"
result "usb command lists the disk" $?
grep -aqE 'sectors=[1-9][0-9]*' "$LOG"
result "disk reports a sector count" $?

# Keyboard end-to-end: type "version" over USB HID via the monitor.
# The assert marker must be unique to the shell command's output (the
# loader banner also contains "NeutrinoOS v0.1").
VCOUNT_BEFORE=$(grep -ac 'NeutrinoOS 1.0.0 x86_64' "$LOG" 2>/dev/null || true)
for k in v e r s i o n ret; do
  mon "sendkey $k"
done
TYPED=1
for i in $(seq 1 20); do
  VCOUNT_NOW=$(grep -ac 'NeutrinoOS 1.0.0 x86_64' "$LOG" 2>/dev/null || true)
  if [ "${VCOUNT_NOW:-0}" -gt "${VCOUNT_BEFORE:-0}" ]; then TYPED=0; break; fi
  sleep 1
done
result "USB keyboard typed 'version' (shell ran it)" $TYPED

# Hot-plug: add a second keyboard, then remove it.
BEFORE=$(grep -ac 'HID keyboard bound' "$LOG" 2>/dev/null || true)
mon "device_add usb-kbd,id=kb2"
ADDED=1
for i in $(seq 1 20); do
  NOW=$(grep -ac 'HID keyboard bound' "$LOG" 2>/dev/null || true)
  if [ "${NOW:-0}" -gt "${BEFORE:-0}" ]; then ADDED=0; break; fi
  sleep 1
done
result "hot-plug: second keyboard enumerated" $ADDED

mon "device_del kb2"
wait_for_file "$LOG" "device removed" 20
result "hot-unplug: driver unbound, slot disabled" $?

kill -9 "$QPID" "$CATPID" 2>/dev/null || true
cleanup_qemu

echo "=== usb summary: $PASS PASS / $FAIL FAIL ==="
if [ "$FAIL" -eq 0 ]; then
  echo "=== usb summary: ALL-PASS ==="
  exit 0
fi
echo "=== usb summary: HAS-FAILURES ==="
echo "--- usb log tail ---"
tail -30 "$LOG" 2>/dev/null || true
exit 1
