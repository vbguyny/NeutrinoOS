#!/bin/bash
# Phase 9 Task 2: IPv6 acceptance.
# Validates: link-local + DHCPv6 autoconfiguration, ping6 ::1 loopback,
# ping6 of the DHCPv6 server (a host on the IPv6 link), AAAA resolution.
set -u
cd /root/neutrino

pkill -9 -f 'p9v6d-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9v6d-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9v6d.in /root/p9v6d.log
mkfifo /root/p9v6d.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9v6dvars.fd

( tail -f /root/p9v6d.in | timeout 480 qemu-system-x86_64 -name p9v6d-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9v6dvars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9v6d.log 2>&1 ) &

ok=0
for i in $(seq 1 180); do
  if strings /root/p9v6d.log 2>/dev/null | grep -q 'root-/> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9v6d.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9v6d.in; sleep "$gap"; }

send "dhcp" 12
send "dhcp6" 15
send "ifconfig" 6
send "ping6 -c 2 ::1" 8

tr -d '\r' < /root/p9v6d.log > /root/p9v6d.txt
SRV=$(grep -a 'DHCPv6 address' /root/p9v6d.txt | tail -1 | sed 's/.*server //; s/[,)].*//' | tr -d ' \r')
echo "dhcp6 server: [$SRV]"
if [ -n "${SRV:-}" ]; then
  send "ping6 -c 2 $SRV" 25
fi
send "dns6 one.one.one.one" 20
send "netstat -s" 6

sleep 2
pkill -9 -f 'p9v6d-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9v6d.log > /root/p9v6d.txt

echo "=== summary markers ==="
grep -a 'leased 10\|DHCPv6 address\|inet6\|64 bytes from\|packets transmitted\|-> \|ADVERTISE\|REPLY' /root/p9v6d.txt | grep -av 'JitStubs\|MetaInt' | head -30
