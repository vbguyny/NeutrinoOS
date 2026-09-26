#!/bin/bash
# Phase 9 Task 2: IPv6 network test (SLAAC via slirp RA + ping6 to router).
set -u
cd /root/neutrino

pkill -9 -f 'p9v6b-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9v6b-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/p9v6b.in /root/p9v6b.log
mkfifo /root/p9v6b.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9v6bvars.fd

( tail -f /root/p9v6b.in | timeout 420 qemu-system-x86_64 -name p9v6b-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9v6bvars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9v6b.log 2>&1 ) &

ok=0
for i in $(seq 1 180); do
  if strings /root/p9v6b.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9v6b.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9v6b.in; sleep "$gap"; }

# 1. Bring IPv6 up (RS -> RA -> SLAAC).
send "ifconfig eth0 up" 20
tr -d '\r' < /root/p9v6b.log > /root/p9v6b.txt
echo "=== bring-up markers ==="
grep -a 'IPv6\|inet6\|link-local\|SLAAC\|router' /root/p9v6b.txt | tail -10

# 2. Extract the router address and SLAAC address from the log.
ROUTER=$(grep -a '\[NetStack\] IPv6 router:' /root/p9v6b.txt | tail -1 | sed 's/.*router: //' | tr -d '\r')
SLAAC=$(grep -a '\[NetStack\] IPv6 SLAAC address:' /root/p9v6b.txt | tail -1 | sed 's/.*address: //' | tr -d '\r')
echo "router=$ROUTER slaac=$SLAAC"

# 3. Show the interface (should carry the SLAAC global address).
send "ifconfig" 6

# 4. Ping the router (slirp answers for its own address).
if [ -n "${ROUTER:-}" ]; then
  send "ping6 -c 2 $ROUTER" 30
fi

# 5. AAAA resolution through slirp's DNS (DNSv6 learned via RDNSS or the v4 server).
send "dns6 one.one.one.one" 20

sleep 2
pkill -9 -f 'p9v6b-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9v6b.log > /root/p9v6b.txt
echo "=== final v6 markers ==="
grep -a 'inet6\|64 bytes\|packets transmitted\|SLAAC\|router:\|->' /root/p9v6b.txt | tail -30
