#!/bin/bash
# Phase 9 Task 2: IPv6 network probing (RA timing experiment + DHCPv4 for DNS).
set -u
cd /root/neutrino

pkill -9 -f 'p9v6c-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9v6c-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9v6c.in /root/p9v6c.log /root/p9v6c.pcap
mkfifo /root/p9v6c.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9v6cvars.fd

( tail -f /root/p9v6c.in | timeout 480 qemu-system-x86_64 -name p9v6c-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9v6cvars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -object filter-dump,id=d0,netdev=n0,file=/root/p9v6c.pcap \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9v6c.log 2>&1 ) &

ok=0
for i in $(seq 1 180); do
  if strings /root/p9v6c.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9v6c.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9v6c.in; sleep "$gap"; }

send "dhcp" 10
send "ifconfig eth0 up" 25
send "ifconfig" 6
send "dns6 one.one.one.one" 20
send "ifconfig" 6

sleep 2
pkill -9 -f 'p9v6c-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9v6c.log > /root/p9v6c.txt
echo "=== RA / SLAAC / router markers ==="
grep -a 'IPv6 router\|SLAAC\|leased\|dns6\|inet6\|link-local up' /root/p9v6c.txt | head -20
echo "=== dns6 result ==="
grep -a -- '->' /root/p9v6c.txt | tail -5
echo "=== pcap v6 ==="
python3 /root/p9-pcap6.py /root/p9v6c.pcap 2>/dev/null | tail -20
