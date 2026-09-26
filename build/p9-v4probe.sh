#!/bin/bash
# Probe IPv4 ARP + DNS behavior in the same environment.
set -u
cd /root/neutrino

pkill -9 -f 'p9v4p-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9v4p-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9v4p.in /root/p9v4p.log /root/p9v4p.pcap
mkfifo /root/p9v4p.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9v4pvars.fd

( tail -f /root/p9v4p.in | timeout 180 qemu-system-x86_64 -name p9v4p-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9v4pvars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -object filter-dump,id=d0,netdev=n0,file=/root/p9v4p.pcap \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9v4p.log 2>&1 ) &

ok=0
for i in $(seq 1 120); do
  if strings /root/p9v4p.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9v4p.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9v4p.in; sleep "$gap"; }

send "ifconfig eth0" 8
send "ping -c 1 10.0.2.2" 12
send "ping -c 1 10.0.2.3" 12
send "dns one.one.one.one" 12

sleep 1
pkill -9 -f 'p9v4p-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9v4p.log > /root/p9v4p.txt

echo "=== outputs ==="
grep -a 'inet \|10.0.2\|reply\|bytes from\|-> \|timed out\|ARP' /root/p9v4p.txt | head -25
echo "=== arp frames ==="
python3 /root/p9-pcapother.py /root/p9v4p.pcap 2>/dev/null | head -20
