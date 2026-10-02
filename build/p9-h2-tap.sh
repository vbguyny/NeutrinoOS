#!/bin/bash
# Phase 9 Task 3: HTTP/2 + TLS/ALPN acceptance over a TAP link (no slirp).
# The WSL host owns 10.0.2.2/24 on tap n0tap; the guest is 10.0.2.15/24.
set -u
cd /root/neutrino

pkill -9 -f 'p9tap-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9tap-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

ip link del n0tap 2>/dev/null || true
ip tuntap add dev n0tap mode tap
ip addr add 10.0.2.2/24 dev n0tap
ip link set n0tap up

rm -f /root/p9tap.in /root/p9tap.log /root/p9tap.pcap
mkfifo /root/p9tap.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9tapvars.fd

( tail -f /root/p9tap.in | timeout 300 qemu-system-x86_64 -name p9tap-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9tapvars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev tap,id=n0,ifname=n0tap,script=no,downscript=no \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -object filter-dump,id=d0,netdev=n0,file=/root/p9tap.pcap \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9tap.log 2>&1 ) &

ok=0
for i in $(seq 1 120); do
  if strings /root/p9tap.log 2>/dev/null | grep -q 'root-/> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9tap.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9tap.in; sleep "$gap"; }

send "ifconfig eth0 up" 20
send "webhost start" 8

echo "=== host: ping guest ==="
ping -c 2 -W 3 10.0.2.15 | tail -2

echo "=== curl: http/1.1 ==="
curl -sS -m 12 --http1.1 -v http://10.0.2.15/health -o /root/p9tap-h1.body 2> /root/p9tap-h1.txt
echo "h1 rc=$?"
grep -a '< HTTP/' /root/p9tap-h1.txt | head -2
cat /root/p9tap-h1.body 2>/dev/null; echo

echo "=== curl: prior knowledge ==="
curl -sS -m 12 --http2-prior-knowledge -v http://10.0.2.15/ -o /root/p9tap-prior.body 2> /root/p9tap-prior.txt
echo "prior rc=$?"
grep -a '< HTTP/2\|< HTTP/' /root/p9tap-prior.txt | head -3
head -c 120 /root/p9tap-prior.body; echo

echo "=== curl: h2c upgrade ==="
curl -sS -m 12 --http2 -v http://10.0.2.15/health -o /root/p9tap-up.body 2> /root/p9tap-up.txt
echo "upgrade rc=$?"
grep -a '< HTTP/2\|< HTTP/1.1 101\|< HTTP/' /root/p9tap-up.txt | head -4
cat /root/p9tap-up.body 2>/dev/null; echo

echo "=== curl: h2 second request on same conn ==="
curl -sS -m 12 --http2-prior-knowledge -v http://10.0.2.15/time http://10.0.2.15/health -o /root/p9tap-two.txt 2> /root/p9tap-two.log
echo "two rc=$?"
grep -ac '< HTTP/2 200' /root/p9tap-two.log
head -c 200 /root/p9tap-two.txt; echo

echo "=== curl: TLS h2 (ALPN) ==="
curl -sS -k -m 15 --http2 -v https://10.0.2.15/health -o /root/p9tap-tls.body 2> /root/p9tap-tls.txt
echo "tls rc=$?"
grep -a 'ALPN\|< HTTP/2\|< HTTP/1' /root/p9tap-tls.txt | head -4
cat /root/p9tap-tls.body 2>/dev/null; echo

echo "=== curl: TLS http/1.1 ==="
curl -sS -k -m 15 --http1.1 -v https://10.0.2.15/health -o /root/p9tap-tls1.body 2> /root/p9tap-tls1.txt
echo "tls1 rc=$?"
grep -a '< HTTP/\|ALPN' /root/p9tap-tls1.txt | head -3
cat /root/p9tap-tls1.body 2>/dev/null; echo

sleep 1
send "webhost status" 3
sleep 1
pkill -9 -f 'p9tap-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9tap.log > /root/p9tap.txt
echo "=== guest web log ==="
grep -a '\[web\]' /root/p9tap.txt | head -14
echo "=== wire summary ==="
python3 /root/p9-pcapother.py /root/p9tap.pcap 2>/dev/null | awk '{print $6}' | sort | uniq -c | sort -rn | head -8
echo "=== faults ==="
grep -a 'RAWV' /root/p9tap.txt | head -3

ip link del n0tap 2>/dev/null || true
