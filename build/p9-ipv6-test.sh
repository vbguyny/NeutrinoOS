#!/bin/bash
# Phase 9 Task 2: IPv6 smoke test.
# Boots the NIC image, brings IPv6 up, pings ::1 (end-to-end through the
# stack loopback), sends a Router Solicitation and reports what the
# network answers (QEMU slirp IPv6 behavior varies).
set -u
cd /root/neutrino

pkill -9 -f 'p9v6-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9v6-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9v6.in /root/p9v6.log
mkfifo /root/p9v6.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9v6vars.fd

( tail -f /root/p9v6.in | timeout 300 qemu-system-x86_64 -name p9v6-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9v6vars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9v6.log 2>&1 ) &

ok=0
for i in $(seq 1 180); do
  if strings /root/p9v6.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9v6.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9v6.in; sleep "$gap"; }

send "ifconfig" 4
send "ping6 -c 2 ::1" 8
send "ifconfig eth0 up" 10
send "ifconfig" 4
send "netstat -s" 4

sleep 2
pkill -9 -f 'p9v6-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9v6.log > /root/p9v6.txt
echo "=== v6 markers ==="
grep -a 'link-local\|SLAAC\|inet6\|64 bytes from\|packets transmitted\|IPv6' /root/p9v6.txt | head -30
echo "=== RS/RA/NDP (stack debug) ==="
grep -a 'NetStack\] IPv6\|Router\|NDP\|icmp6' /root/p9v6.txt | head -20
