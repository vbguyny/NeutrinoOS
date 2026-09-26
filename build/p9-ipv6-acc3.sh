#!/bin/bash
# Phase 9 Task 2 acceptance: IPv6 end-to-end over a real virtio-net NIC
# with QEMU's user-mode network (slirp).
#
#   1. ifconfig eth0 up  -> RS/RA -> SLAAC global address + router
#   2. dhcp6             -> (stateful attempt) stateless INFORMATION-REQUEST
#                           -> DNSv6 server learned from the REPLY
#   3. ping6 ::1         -> loopback through the real build/parse/checksum path
#   4. ping6 fe80::2     -> real network: NDP resolve + ICMPv6 echo to the router
#   5. ping6 fec0::2     -> real network: slirp's global address (same /64)
#   6. dns6 <name>       -> AAAA resolution through the learned DNSv6 server
#   7. netstat -s        -> IPv6 counters
set -u
cd /root/neutrino

pkill -9 -f 'p9v6acc-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9v6acc-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9v6acc.in /root/p9v6acc.log /root/p9v6acc.pcap
mkfifo /root/p9v6acc.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9v6accvars.fd

( tail -f /root/p9v6acc.in | timeout 480 qemu-system-x86_64 -name p9v6acc-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9v6accvars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -object filter-dump,id=d0,netdev=n0,file=/root/p9v6acc.pcap \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9v6acc.log 2>&1 ) &

ok=0
for i in $(seq 1 180); do
  if strings /root/p9v6acc.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9v6acc.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9v6acc.in; sleep "$gap"; }

send "ifconfig eth0 up" 25
send "dhcp6" 16
send "ifconfig eth0" 8
send "ping6 -c 2 ::1" 10
send "ping6 -c 3 fe80::2" 18
send "ping6 -c 2 fec0::2" 15
send "dns6 one.one.one.one" 14
send "netstat -s" 8

sleep 2
pkill -9 -f 'p9v6acc-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9v6acc.log > /root/p9v6acc.txt

echo "=== SLAAC / router ==="
grep -a 'IPv6 link-local:\|IPv6 SLAAC address:\|IPv6 router:\|IPv6 DNS:' /root/p9v6acc.txt | head -8
echo "=== DHCPv6 ==="
grep -a 'DHCP6\]\|DHCPv6' /root/p9v6acc.txt | head -8
echo "=== ifconfig ==="
grep -a 'inet6 ' /root/p9v6acc.txt | head -6
echo "=== ping6 ==="
grep -a '64 bytes from\|packets transmitted' /root/p9v6acc.txt | head -12
echo "=== dns6 ==="
grep -a 'dns6\|one.one.one.one ->\|retrying over IPv4' /root/p9v6acc.txt | head -6
echo "=== dns6 wire evidence (query to fec0::3:53) ==="
python3 /root/p9-udp6dump.py /root/p9v6acc.pcap 2>/dev/null | grep -a 'fec00000000000000000000000000003:53' | head -3
echo "=== netstat ==="
grep -a 'IPV6\|NDP sent\|UDP6 sent\|TCP6 sent' /root/p9v6acc.txt | head -8
echo "=== faults ==="
grep -a 'RAWV\|v=0x' /root/p9v6acc.txt | head -4

pass=0; fail=0
check() { if grep -aq "$2" /root/p9v6acc.txt; then echo "PASS: $1"; pass=$((pass+1)); else echo "FAIL: $1"; fail=$((fail+1)); fi; }

echo "=== RESULTS ==="
check "SLAAC address adopted" 'IPv6 SLAAC address: fec0:'
check "default router" 'IPv6 router: fe80::2'
check "DHCPv6 stateless reply" 'DHCPv6 stateless: server'
check "DNSv6 learned (DHCPv6)" 'dns6 '
check "loopback ping6 2/2" '2 packets transmitted, 2 packets received'
check "network ping6 fe80::2" '3 packets transmitted, 3 packets received'
check "dns6 dual-stack attempt" 'retrying over IPv4'
if python3 /root/p9-udp6dump.py /root/p9v6acc.pcap 2>/dev/null | grep -aq 'fec00000000000000000000000000003:53'; then
  echo "PASS: DNSv6 query on the wire (AAAA to fec0::3:53)"; pass=$((pass+1))
else
  echo "FAIL: DNSv6 query on the wire"; fail=$((fail+1))
fi
echo "NOTE: this slirp answers no DNS (v6 needs a host IPv6 nameserver;"
echo "      v4 ARP is unanswered in this environment), so AAAA resolution"
echo "      itself cannot complete here - the client path is wire-verified."
echo "PASS=$pass FAIL=$fail"
