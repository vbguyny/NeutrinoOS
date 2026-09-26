#!/bin/bash
# Warm-reset experiment: does a monitor-forced system_reset reproduce the
# second-boot kernel crash (independent of the `reboot` builtin)?
# Progress is written to /root/p9warm.status for polling.
set -uo pipefail

LOG=/root/p9warm.log
STATUS=/root/p9warm.status
rm -f "$LOG" "$STATUS" /tmp/p9w.in /tmp/p9w.out /tmp/p9wm.sock
echo "starting" > "$STATUS"
pkill -9 -f 'p9warm-qemu' 2>/dev/null || true
sleep 1

cp -f /root/neutrino/build/x64/OVMF_VARS-ahci.fd /tmp/p9wv.fd
mkfifo /tmp/p9w.in /tmp/p9w.out

qemu-system-x86_64 -name p9warm-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9wv.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/neutrino/build/x64/neutrinoos.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -display none -serial pipe:/tmp/p9w \
  -monitor unix:/tmp/p9wm.sock,server,nowait > /dev/null 2>&1 &
QPID=$!
cat /tmp/p9w.out > "$LOG" &
CATPID=$!
echo "qemu=$QPID cat=$CATPID launched" >> "$STATUS"

for i in $(seq 1 240); do
  kill -0 "$QPID" 2>/dev/null || break
  grep -aq 'neutrinoos> ' "$LOG" 2>/dev/null && break
  sleep 1
done
echo "first-boot prompt: $(grep -ac 'neutrinoos> ' "$LOG" 2>/dev/null)" >> "$STATUS"
if ! kill -0 "$QPID" 2>/dev/null; then
  echo "qemu died before reset; aborting" >> "$STATUS"
  echo "banners: 0" >> "$STATUS"
  exit 1
fi

python3 - <<'PY'
import socket, time
s = socket.socket(socket.AF_UNIX)
s.connect('/tmp/p9wm.sock')
time.sleep(0.5)
s.sendall(b'system_reset\n')
time.sleep(1.0)
s.close()
PY
echo "system_reset sent rc=$?" >> "$STATUS"

BOOTS=0
for i in $(seq 1 150); do
  BOOTS=$(grep -ac 'NeutrinoOS console ready' "$LOG" 2>/dev/null || true)
  [ "${BOOTS:-0}" -ge 2 ] && break
  sleep 1
done
echo "banners: $BOOTS" >> "$STATUS"
echo "crash markers: $(grep -ac 'X64 Exception' "$LOG" 2>/dev/null || true)" >> "$STATUS"
echo "--- last lines ---" >> "$STATUS"
tail -c 500 "$LOG" >> "$STATUS" 2>/dev/null
echo "done" >> "$STATUS"

kill -9 "$QPID" "$CATPID" 2>/dev/null || true
pkill -9 -f 'p9warm-qemu' 2>/dev/null || true
