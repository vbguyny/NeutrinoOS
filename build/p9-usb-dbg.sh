#!/bin/bash
# Interrogate the running VM: QEMU's USB view + raw MMIO reads.
set -uo pipefail
LOG=/root/p9dbg.log
SOCK=/tmp/p9dbg.sock
STICK=/root/p9usb-stick.img
rm -f "$LOG" "$SOCK" /tmp/p9d.ser.in /tmp/p9d.ser.out
pkill -9 -f 'p9dbg-qem[u]' 2>/dev/null || true
sleep 1
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd /tmp/p9dv.fd
mkfifo /tmp/p9d.ser.in /tmp/p9d.ser.out

qemu-system-x86_64 -name p9dbg-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9dv.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/neutrino/build/x64/neutrinoos.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -device qemu-xhci -device usb-kbd \
  -drive id=usbstick,if=none,format=raw,file="$STICK" \
  -device usb-storage,drive=usbstick \
  -display none -serial pipe:/tmp/p9d.ser \
  -monitor unix:"$SOCK",server,nowait > /dev/null 2>&1 &
QPID=$!
cat /tmp/p9d.ser.out > "$LOG" &
CATPID=$!

for i in $(seq 1 240); do
  grep -aq 'neutrinoos> ' "$LOG" 2>/dev/null && break
  sleep 1
done
sleep 2

mon() {
python3 - "$SOCK" "$1" <<'PY'
import socket, sys, time
s = socket.socket(socket.AF_UNIX)
s.settimeout(3.0)
s.connect(sys.argv[1])
time.sleep(0.3)
try: s.recv(8192)
except Exception: pass
s.sendall(sys.argv[2].encode() + b'\n')
time.sleep(0.8)
try:
    print(s.recv(16384).decode(errors='replace'))
except Exception as e:
    print("(no reply)", e)
s.close()
PY
}

echo "===== info usb ====="
mon "info usb"
echo "===== xp caplen 0xc000000000 ====="
mon "xp /16bx 0xc000000000"
echo "===== xp portsc0 0xc0000400 ====="
mon "xp /4wx 0xc0000400"
echo "===== xp portsc1 0xc0000410 ====="
mon "xp /4wx 0xc0000410"
echo "===== xp portsc4 0xc0000440 ====="
mon "xp /4wx 0xc0000440"
echo "===== guest usb cmd ====="
printf 'usb\r' > /tmp/p9d.ser.in
sleep 3
grep -a 'usb\]' "$LOG" | tail -8

kill -9 "$QPID" "$CATPID" 2>/dev/null || true
pkill -9 -f 'p9dbg-qem[u]' 2>/dev/null || true
