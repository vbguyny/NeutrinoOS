#!/bin/bash
# Phase 9 Task 3: in-guest HTTP/2 test (h2test utility).
set -u
cd /root/neutrino

pkill -9 -f 'p9h2t-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9h2t-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9h2t.in /root/p9h2t.log
mkfifo /root/p9h2t.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9h2tvars.fd

( tail -f /root/p9h2t.in | timeout 240 qemu-system-x86_64 -name p9h2t-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9h2tvars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9h2t.log 2>&1 ) &

ok=0
for i in $(seq 1 120); do
  if strings /root/p9h2t.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9h2t.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9h2t.in; sleep "$gap"; }

send "webhost start" 8
send "h2test" 35

sleep 1
pkill -9 -f 'p9h2t-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9h2t.log > /root/p9h2t.txt

echo "=== h2test output ==="
grep -a 'h2test\|PUSH_PROMISE' /root/p9h2t.txt | head -20
echo "=== guest web log ==="
grep -a '\[web\]' /root/p9h2t.txt | tail -10
echo "=== faults ==="
grep -a 'RAWV' /root/p9h2t.txt | head -3
grep -ac 'h2test: PASS' /root/p9h2t.txt
