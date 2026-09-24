#!/bin/bash
# Phase 6: boot a persistent NeutrinoOS VM for host-side (Windows) tests.
# - deploys the current build to /root/run.img
# - installs users + a public key for passwordless ssh as user 'user'
# - prepares /etc/boot.params so sshd + webhost autostart
# - runs QEMU with hostfwd 2222->22, 8080->80, 8444->443
# The VM keeps running until `pkill qemu-system` (see the trap note below);
# this script is meant to be started in the background by
# tests/run-phase6-tests.ps1.
set -u
cd /root/neutrino
export MTOOLS_SKIP_CHECK=1
echo "[$(date +%T)] p6-qemu-serve start"
pkill -9 qemu-system 2>/dev/null || true
sleep 1

if [ ! -f /root/p6key ]; then
  ssh-keygen -t ed25519 -N "" -f /root/p6key -q
fi
cp /root/p6key.pub /mnt/d/Projects/Code/NeutrinoOS/build/authorized_keys.pub

bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-deploy.sh > /root/p6serve-deploy.log 2>&1
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-image-extras.sh >> /root/p6serve-deploy.log 2>&1
sleep 1

printf 'net.ip=dhcp\nsshd.autostart=yes\nwebhost.autostart=yes\n' > /tmp/p6serve-boot.params
timeout -s KILL 20 mcopy -i /root/run.img -o /tmp/p6serve-boot.params ::/etc/boot.params >/dev/null 2>&1 || true

cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin /root/p6serve.log
mkfifo /root/qin

echo "[$(date +%T)] launching QEMU (persistent)..."
( tail -f /root/qin | qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,hostfwd=tcp::2222-:22,hostfwd=tcp::8080-:80,hostfwd=tcp::8444-:443 \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p6serve.log 2>&1 ) &
echo "[$(date +%T)] qemu started; log /root/p6serve.log"
wait
