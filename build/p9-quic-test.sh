#!/bin/bash
# Phase 9 Task 3: HTTP/3 over QUIC acceptance via TAP (aioquic client).
set -u
cd /root/neutrino

pkill -9 -f 'p9h3-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9h3-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

ip link del n0tap 2>/dev/null || true
ip tuntap add dev n0tap mode tap
ip addr add 10.0.2.2/24 dev n0tap
ip link set n0tap up

rm -f /root/p9h3.in /root/p9h3.log /root/p9h3.pcap
mkfifo /root/p9h3.in

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
# Remove any previously generated certificate so the fixed validity
# encoder regenerates it on this boot.
mdel -i /root/run.img ::/etc/ssl/certs/neutrinoos.crt ::/etc/ssl/private/neutrinoos.key 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9h3vars.fd

( tail -f /root/p9h3.in | timeout 180 qemu-system-x86_64 -name p9h3-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9h3vars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev tap,id=n0,ifname=n0tap,script=no,downscript=no \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -object filter-dump,id=d0,netdev=n0,file=/root/p9h3.pcap \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9h3.log 2>&1 ) &

ok=0
for i in $(seq 1 120); do
  if strings /root/p9h3.log 2>/dev/null | grep -q 'root-/> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9h3.log; exit 1; fi
sleep 2

send() { local line="$1" gap="${2:-6}"; echo "$line" > /root/p9h3.in; sleep "$gap"; }

send "ifconfig eth0 up" 20
send "webhost start" 8

echo "=== aioquic HTTP/3 GET /health ==="
timeout 25 python3 /root/p9-h3-client.py 10.0.2.15 443 /health 2>&1 | grep -av '^DEBUG'
echo "=== aioquic HTTP/3 GET / ==="
timeout 25 python3 /root/p9-h3-client.py 10.0.2.15 443 / 2>&1 | tail -4

sleep 1
pkill -9 -f 'p9h3-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9h3.log > /root/p9h3.txt
echo "=== guest web log ==="
grep -a '\[web\]\|QUIC\|quic\|RAWW\|RAWV' /root/p9h3.txt | tail -10
echo "=== udp frames ==="
python3 - <<'EOF'
import struct
d=open('/root/p9h3.pcap','rb').read()
off=24; n=0
while off < len(d):
    ts,tu,cl,cu=struct.unpack('<IIII', d[off:off+16])
    frame=d[off+16:off+16+cl]
    off+=16+cl
    if len(frame)<34: continue
    et=struct.unpack('>H',frame[12:14])[0]
    if et!=0x0800: continue
    ip=frame[14:]
    if ip[9]!=17: continue
    tot=struct.unpack('>H',ip[2:4])[0]
    ihl=(ip[0]&0xf)*4
    sport,dport=struct.unpack('>HH',ip[ihl:ihl+4])
    p=ip[ihl+8:tot]
    n+=1
    if n<=8:
        typ=(p[0]>>4)&3
        print(f'udp {sport}->{dport} len={len(p)} first=0x{p[0]:02x} type={typ}')
print('total udp frames', n)
EOF

ip link del n0tap 2>/dev/null || true
