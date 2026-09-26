#!/bin/bash
# TLS isolate: no-ALPN curl + openssl s_client.
set -u
cd /root/neutrino

pkill -9 -f 'p9tls-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9tls-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

ip link del n0tap 2>/dev/null || true
ip tuntap add dev n0tap mode tap
ip addr add 10.0.2.2/24 dev n0tap
ip link set n0tap up

rm -f /root/p9tls.in /root/p9tls.log
mkfifo /root/p9tls.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9tlsvars.fd

( tail -f /root/p9tls.in | timeout 240 qemu-system-x86_64 -name p9tls-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9tlsvars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev tap,id=n0,ifname=n0tap,script=no,downscript=no \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9tls.log 2>&1 ) &

ok=0
for i in $(seq 1 120); do
  if strings /root/p9tls.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9tls.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9tls.in; sleep "$gap"; }

send "ifconfig eth0 up" 20
send "webhost start" 8

echo "=== curl --no-alpn h1.1 ==="
curl -sS -k -m 10 --no-alpn -v --http1.1 https://10.0.2.15/health -o /root/p9tls-noa.body 2> /root/p9tls-noa.txt
echo "noalpn rc=$?"
grep -a 'TLSv1.3\|error\|< HTTP' /root/p9tls-noa.txt | tail -8
cat /root/p9tls-noa.body 2>/dev/null; echo

echo "=== openssl s_client (alpn h2) ==="
echo Q | timeout 15 openssl s_client -connect 10.0.2.15:443 -tls1_3 -alpn h2 -quiet 2> /root/p9tls-ossl.txt | head -5
echo "ossl rc=$?"
tail -12 /root/p9tls-ossl.txt

sleep 1
pkill -9 -f 'p9tls-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9tls.log > /root/p9tls.txt
echo "=== server tls log ==="
grep -a 'tls:\|record decrypt' /root/p9tls.txt | tail -20

ip link del n0tap 2>/dev/null || true
