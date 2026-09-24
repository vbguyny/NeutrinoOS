#!/bin/bash
# Phase 6: firewall test - /etc/firewall.conf denies TCP 443 but allows 80.
set -u
cd /root/neutrino
exec < /dev/null
export MTOOLS_SKIP_CHECK=1
echo "[$(date +%T)] p6-firewall-test start"
pkill -9 qemu-system 2>/dev/null || true
sleep 1

cleanup() {
  pkill -9 -f qemu-system 2>/dev/null || true
  rm -f /root/qin
}
trap cleanup EXIT

bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-deploy.sh > /root/p6fwdeploy.log 2>&1
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-image-extras.sh >> /root/p6fwdeploy.log 2>&1
sleep 1

printf 'net.ip=dhcp\nwebhost.autostart=yes\n' > /tmp/p6fw-boot.params
printf 'deny tcp 443\n' > /tmp/p6fw-firewall.conf
timeout -s KILL 20 mcopy -i /root/run.img -o /tmp/p6fw-boot.params ::/etc/boot.params >/dev/null 2>&1 || true
timeout -s KILL 20 mcopy -i /root/run.img -o /tmp/p6fw-firewall.conf ::/etc/firewall.conf >/dev/null 2>&1 || true

cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin /root/p6fw.log
mkfifo /root/qin

( tail -f /root/qin | timeout 200 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,hostfwd=tcp::8080-:80,hostfwd=tcp::8444-:443 \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p6fw.log 2>&1 ) &

for i in $(seq 1 90); do
  if strings /root/p6fw.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; break; fi
  sleep 1
done
sleep 3

echo "===== HTTP /health (should be 200) ====="
timeout 20 curl -s -H 'Connection: close' -o - -w '\ncode=%{http_code}\n' http://127.0.0.1:8080/health
echo "===== HTTPS /health (should fail: denied port 443) ====="
timeout 15 curl -sk --max-time 8 -o /dev/null -w 'code=%{http_code}\n' https://127.0.0.1:8444/health || echo "https refused (expected)"

echo "===== guest firewall log ====="
strings /root/p6fw.log | grep -a -E 'firewall|denied|\[web\]' | tail -12
echo "===== SYSTEM HALTED count ====="
strings /root/p6fw.log | grep -sc 'SYSTEM HALTED' || true
