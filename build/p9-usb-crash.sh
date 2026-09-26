#!/bin/bash
# Boot USB config, capture registers right after the crash.
set -uo pipefail
SOCK=/tmp/p9crash.sock
STICK=/root/p9usb-stick.img
rm -f "$SOCK" /tmp/p9c.ser.in /tmp/p9c.ser.out
pkill -9 -f 'p9crash-qem[u]' 2>/dev/null || true
sleep 1
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd /tmp/p9cv.fd
mkfifo /tmp/p9c.ser.in /tmp/p9c.ser.out

qemu-system-x86_64 -name p9crash-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9cv.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/neutrino/build/x64/neutrinoos.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -device qemu-xhci -device usb-kbd \
  -drive id=usbstick,if=none,format=raw,file="$STICK" \
  -device usb-storage,drive=usbstick \
  -display none -serial pipe:/tmp/p9c.ser \
  -monitor unix:"$SOCK",server,nowait -no-reboot -no-shutdown > /dev/null 2>&1 &
QPID=$!
cat /tmp/p9c.ser.out > /tmp/p9crash.log &
CATPID=$!
sleep 15

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
    print(s.recv(65536).decode(errors='replace'))
except Exception as e:
    print("(no reply)", e)
s.close()
PY
}

echo "===== info registers ====="
mon "info registers" | grep -E 'RIP|RAX|RBX|RCX|RDX|RSI|RDI|RSP|RBP|R8|R9|R1|CR2|CR3' | head -20
echo "===== tail ====="
tail -c 400 /tmp/p9crash.log | tr -d '\r'

kill -9 "$QPID" "$CATPID" 2>/dev/null || true
pkill -9 -f 'p9crash-qem[u]' 2>/dev/null || true
