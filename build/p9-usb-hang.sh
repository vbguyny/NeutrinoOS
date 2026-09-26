#!/bin/bash
# Capture RIP of the hung VM and symbolize it.
set -uo pipefail
SOCK=/tmp/p9hang.sock
STICK=/root/p9usb-stick.img
rm -f "$SOCK" /tmp/p9h.ser.in /tmp/p9h.ser.out "$SOCK"
pkill -9 -f 'p9hang-qem[u]' 2>/dev/null || true
sleep 1
cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd /tmp/p9hv.fd
mkfifo /tmp/p9h.ser.in /tmp/p9h.ser.out

qemu-system-x86_64 -name p9hang-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9hv.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/neutrino/build/x64/neutrinoos.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -device qemu-xhci -device usb-kbd \
  -drive id=usbstick,if=none,format=raw,file="$STICK" \
  -device usb-storage,drive=usbstick \
  -display none -serial pipe:/tmp/p9h.ser \
  -monitor unix:"$SOCK",server,nowait > /dev/null 2>&1 &
QPID=$!
cat /tmp/p9h.ser.out > /tmp/p9hang.log &
CATPID=$!
sleep 20

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

echo "===== info status ====="
mon "info status"
echo "===== info registers ====="
mon "info registers" | head -30

RIP=$(mon "info registers" | grep -oP 'RIP=\K[0-9a-f]+' | head -1)
echo "RIP=$RIP"
if [ -n "${RIP:-}" ]; then
  RT=$((0x$RIP))
  if [ $RT -ge $((0x8000000)) ] && [ $RT -lt $((0x40000000)) ]; then
    ELF=$((0x140000000 + RT - 0x8000000))
    echo "ELFaddr=0x$(printf '%x' $ELF)"
    gdb -batch -ex "info symbol 0x$(printf '%x' $ELF)" /root/neutrino/build/x64/kernel_syms.elf 2>/dev/null
  fi
fi
echo "===== serial tail ====="
tail -c 600 /tmp/p9hang.log | tr -d '\r'

kill -9 "$QPID" "$CATPID" 2>/dev/null || true
pkill -9 -f 'p9hang-qem[u]' 2>/dev/null || true
