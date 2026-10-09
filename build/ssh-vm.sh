#!/bin/bash
# Boot the SSH-ready CLI image with guest port 22 forwarded to host 2222.
# Prepare the image first:  bash build/ssh-cli-image.sh [your-key.pub]
# Then in the VM shell:     sshd
# From Windows 11:          ssh -p 2222 user@127.0.0.1
# Docs: docs/SSH.md
set -e
cd "$(dirname "$0")/.."

IMG=build/neutrinoos-cli.img
[ -f "$IMG" ] || {
  echo "missing $IMG - run: wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/rebuild-cli-image.sh"
  echo "then prepare it:        wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/ssh-cli-image.sh"
  exit 1
}

# Fresh copies: a raw image and the UEFI variable store both get written to.
cp -f "$IMG" /tmp/neutrinoos-ssh.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/neutrinoos-ssh.vars.fd

echo "booting... host port 2222 -> guest port 22 (type 'sshd' at the shell; then: ssh -p 2222 user@127.0.0.1)"
exec qemu-system-x86_64 -machine q35 -m 2G -smp 2 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/neutrinoos-ssh.vars.fd \
  -drive format=raw,file=/tmp/neutrinoos-ssh.img \
  -netdev user,id=n0,hostfwd=tcp::2222-:22 \
  -device virtio-net-pci,netdev=n0 \
  -vga std -serial mon:stdio -no-reboot
