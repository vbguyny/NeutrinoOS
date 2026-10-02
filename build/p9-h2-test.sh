#!/bin/bash
# Phase 9 Task 3: HTTP/2 h2c test - prior knowledge + Upgrade + h1.1.
set -u
cd /root/neutrino

pkill -9 -f 'p9h2-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9h2-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9h2.in /root/p9h2.log
mkfifo /root/p9h2.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9h2vars.fd

( tail -f /root/p9h2.in | timeout 300 qemu-system-x86_64 -name p9h2-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9h2vars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on,hostfwd=tcp::8080-10.0.2.15:80,hostfwd=tcp::8443-10.0.2.15:443 \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -object filter-dump,id=d0,netdev=n0,file=/root/p9h2.pcap \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9h2.log 2>&1 ) &

ok=0
for i in $(seq 1 120); do
  if strings /root/p9h2.log 2>/dev/null | grep -q 'root-/> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9h2.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9h2.in; sleep "$gap"; }

send "ifconfig eth0 up" 25
send "webhost start" 8

echo "=== curl: prior knowledge ==="
curl -sS -m 12 --http2-prior-knowledge -v http://127.0.0.1:8080/ -o /root/p9h2-prior.body 2> /root/p9h2-prior.txt
echo "prior rc=$?"
grep -a '< HTTP/2\|< HTTP/' /root/p9h2-prior.txt | head -3
head -c 120 /root/p9h2-prior.body; echo

echo "=== curl: h2c upgrade ==="
curl -sS -m 12 --http2 -v http://127.0.0.1:8080/health -o /root/p9h2-up.body 2> /root/p9h2-up.txt
echo "upgrade rc=$?"
grep -a '< HTTP/2\|< HTTP/1.1 101\|< HTTP/' /root/p9h2-up.txt | head -4
cat /root/p9h2-up.body 2>/dev/null; echo

echo "=== curl: http/1.1 fallback ==="
curl -sS -m 12 --http1.1 -v http://127.0.0.1:8080/health -o /root/p9h2-h1.body 2> /root/p9h2-h1.txt
echo "h1 rc=$?"
grep -a '< HTTP/' /root/p9h2-h1.txt | head -2
cat /root/p9h2-h1.body 2>/dev/null; echo

echo "=== curl: h2 second request on same conn ==="
curl -sS -m 12 --http2-prior-knowledge -v http://127.0.0.1:8080/time http://127.0.0.1:8080/health -o /root/p9h2-two.txt 2> /root/p9h2-two.log
echo "two rc=$?"
grep -ac '< HTTP/2 200' /root/p9h2-two.log
head -c 200 /root/p9h2-two.txt; echo

echo "=== curl: TLS h2 (ALPN) ==="
curl -sS -k -m 15 --http2 -v https://127.0.0.1:8443/health -o /root/p9h2-tls.body 2> /root/p9h2-tls.txt
echo "tls rc=$?"
grep -a 'ALPN\|< HTTP/2\|< HTTP/1' /root/p9h2-tls.txt | head -4
cat /root/p9h2-tls.body 2>/dev/null; echo

echo "=== curl: TLS http/1.1 ==="
curl -sS -k -m 15 --http1.1 -v https://127.0.0.1:8443/health -o /root/p9h2-tls1.body 2> /root/p9h2-tls1.txt
echo "tls1 rc=$?"
grep -a '< HTTP/\|ALPN' /root/p9h2-tls1.txt | head -3
cat /root/p9h2-tls1.body 2>/dev/null; echo

sleep 1
send "webhost status" 3
sleep 1
pkill -9 -f 'p9h2-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9h2.log > /root/p9h2.txt
echo "=== guest web log ==="
grep -a '\[web\]' /root/p9h2.txt | head -12
echo "=== guest ifconfig ==="
grep -a 'inet \|eth0' /root/p9h2.txt | head -6
echo "=== wire (non-v6 frames) ==="
python3 /root/p9-pcapother.py /root/p9h2.pcap 2>/dev/null | head -20
echo "=== faults ==="
grep -a 'RAWV' /root/p9h2.txt | head -3
