#!/bin/bash
# Single TLS-h2 curl with h2 frame tracing.
set -u
cd /root/neutrino

pkill -9 -f 'p9th2-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9th2-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

ip link del n0tap 2>/dev/null || true
ip tuntap add dev n0tap mode tap
ip addr add 10.0.2.2/24 dev n0tap
ip link set n0tap up

rm -f /root/p9th2.in /root/p9th2.log /root/p9th2.pcap
mkfifo /root/p9th2.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9th2vars.fd

( tail -f /root/p9th2.in | timeout 150 qemu-system-x86_64 -name p9th2-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9th2vars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev tap,id=n0,ifname=n0tap,script=no,downscript=no \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -object filter-dump,id=d0,netdev=n0,file=/root/p9th2.pcap \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9th2.log 2>&1 ) &

ok=0
for i in $(seq 1 120); do
  if strings /root/p9th2.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9th2.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9th2.in; sleep "$gap"; }

send "ifconfig eth0 up" 20
send "webhost start" 8

echo "=== curl TLS h2 ==="
curl -sS -k -m 12 --http2 -v https://10.0.2.15/health -o /root/p9th2.body 2> /root/p9th2.txt
echo "tls rc=$?"
grep -a 'ALPN\|HTTP/2\|error' /root/p9th2.txt | tail -6
cat /root/p9th2.body 2>/dev/null; echo

sleep 1
pkill -9 -f 'p9th2-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9th2.log > /root/p9th2-clean.txt
echo "=== h2/dbg trace ==="
grep -a 'h2s\|h2r\|FAIL\|RST\|tls: record' /root/p9th2-clean.txt | tail -30

ip link del n0tap 2>/dev/null || true
