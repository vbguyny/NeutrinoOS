#!/bin/bash
# Phase 6: web host + TLS smoke test (QEMU user-net, hostfwd 8080->80, 8443->443).
set -u
cd /root/neutrino
exec < /dev/null
export MTOOLS_SKIP_CHECK=1
echo "[$(date +%T)] p6-web-test start"
pkill -9 qemu-system 2>/dev/null || true
sleep 1

cleanup() {
  pkill -9 -f qemu-system 2>/dev/null || true
  rm -f /root/qin
}
trap cleanup EXIT

echo "[$(date +%T)] deploying image..."
bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-deploy.sh > /root/p6webdeploy.log 2>&1
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-image-extras.sh >> /root/p6webdeploy.log 2>&1
sleep 1

echo "[$(date +%T)] adding /var/www + boot params..."
printf 'static file from /var/www\n' > /tmp/p6web-hello.txt
timeout -s KILL 20 mdir -i /root/run.img ::/var >/dev/null 2>&1 || \
  timeout -s KILL 20 mmd -i /root/run.img ::/var >/dev/null 2>&1 || true
timeout -s KILL 20 mdir -i /root/run.img ::/var/www >/dev/null 2>&1 || \
  timeout -s KILL 20 mmd -i /root/run.img ::/var/www >/dev/null 2>&1 || true
timeout -s KILL 20 mcopy -i /root/run.img -o /tmp/p6web-hello.txt ::/var/www/hello.txt >/dev/null 2>&1 || true
printf 'net.ip=dhcp\nwebhost.autostart=yes\n' > /tmp/p6web-boot.params
timeout -s KILL 20 mcopy -i /root/run.img -o /tmp/p6web-boot.params ::/etc/boot.params >/dev/null 2>&1 || true
printf '# NeutrinoOS local startup\nuname\n' > /tmp/p6web-rclocal
timeout -s KILL 20 mcopy -i /root/run.img -o /tmp/p6web-rclocal ::/etc/rc.local >/dev/null 2>&1 || true

cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin /root/p6web.log
mkfifo /root/qin

echo "[$(date +%T)] launching QEMU..."
( tail -f /root/qin | timeout 240 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,hostfwd=tcp::8080-:80,hostfwd=tcp::8444-:443 \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p6web.log 2>&1 ) &

for i in $(seq 1 90); do
  if strings /root/p6web.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; break; fi
  sleep 1
done
sleep 2

# Services start via /etc/boot.params (net.ip=dhcp, webhost.autostart=yes)
# and /etc/rc.local; no console input needed.
echo "----- boot params / rc.local evidence -----"
strings /root/p6web.log | grep -a -E '\[boot\]|NeutrinoOS 0\.5|\[web\]|\[NetMgr\]|rc.local' | head -12

echo "===== HTTP / ====="
timeout 20 curl -s http://127.0.0.1:8080/ | head -3
echo "===== HTTP /health ====="
timeout 20 curl -s -H 'Connection: close' http://127.0.0.1:8080/health
echo "===== HTTP /time ====="
timeout 20 curl -s -H 'Connection: close' http://127.0.0.1:8080/time | xxd | head -3
echo "===== HTTP /hello.txt (static) ====="
timeout 20 curl -s -H 'Connection: close' http://127.0.0.1:8080/hello.txt
echo "===== HTTP /missing (404) ====="
timeout 20 curl -s -o /dev/null -H 'Connection: close' -w '%{http_code}\n' http://127.0.0.1:8080/missing

echo "===== guest -> host ping (console) ====="
send() {
  timeout 5 bash -c "printf '%s\n' '$1' > /root/qin" 2>/dev/null || echo "!! guest input fifo blocked"
  sleep "$2"
}
send "ping -c 2 10.0.2.2" 6
strings /root/p6web.log | grep -a -E 'reply|icmp|ICMP|bytes from' | tail -4

echo "===== HTTPS /health (TLS 1.3 via capture proxy) ====="
python3 /mnt/d/Projects/Code/NeutrinoOS/build/p6-tls-proxy.py > /root/tlsproxy.log 2>&1 &
TPROXY=$!
sleep 1
timeout 20 curl -sk --tlsv1.3 --tls-max 1.3 -o - -w '\ncode=%{http_code}\n' https://127.0.0.1:8443/health
sleep 1
kill $TPROXY 2>/dev/null || true
echo "===== openssl s_client with keylog ====="
rm -f /root/tls.keys
printf '' | timeout 15 openssl s_client -connect 127.0.0.1:8443 -tls1_3 -keylogfile /root/tls.keys -brief 2>&1 | head -6
printf 'GET /health HTTP/1.1\r\nHost: test\r\nConnection: close\r\n\r\n' | timeout 20 openssl s_client -connect 127.0.0.1:8443 -tls1_3 -quiet 2>&1 | head -8
echo "===== capture (c2s / s2c) ====="
ls -la /root/tlsc2s.bin /root/tlss2c.bin 2>/dev/null
python3 -c "import binascii; d=open('/root/tlss2c.bin','rb').read(); print('s2c head:', binascii.hexlify(d[:64]).decode())"
python3 -c "import binascii; d=open('/root/tlsc2s.bin','rb').read(); print('c2s tail:', binascii.hexlify(d[-96:]).decode())"

cat /root/tlsproxy.log | tail -4

echo "===== guest log tail ====="
strings /root/p6web.log | grep -a -E '\[web\]|\[firewall\]|HALTED|Exception|Unhandled|SYN received' | tail -30
echo "===== SYSTEM HALTED count ====="
strings /root/p6web.log | grep -sc 'SYSTEM HALTED' || true
