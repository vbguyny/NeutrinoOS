#!/bin/bash
# Boot the NeutrinoOS CLI image in QEMU with the sample REST API port
# forwarded to the host (guest 8080 -> host 18080).
#
# From Windows 11 with WSL2: run this inside WSL, then open
#   http://127.0.0.1:18080
# in Windows (WSL2 forwards localhost to the host automatically).
# In the VM console type:  sampleapi start
# Docs: docs/samples/rest-api/README.md
set -e
cd "$(dirname "$0")/.."

IMG=build/neutrinoos-cli.img
[ -f "$IMG" ] || { echo "missing $IMG - run: wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/rebuild-cli-image.sh"; exit 1; }

# Fresh copies: a raw image and the UEFI variable store both get written to.
cp -f "$IMG" /tmp/sampleapi-vm.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/sampleapi-vm.vars.fd

echo "booting... host port 18080 -> guest port 8080 (type 'sampleapi start' at the shell)"
exec qemu-system-x86_64 -machine q35 -m 2G -smp 2 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/sampleapi-vm.vars.fd \
  -drive format=raw,file=/tmp/sampleapi-vm.img \
  -netdev user,id=n0,hostfwd=tcp::18080-:8080 \
  -device virtio-net-pci,netdev=n0 \
  -vga std -serial mon:stdio -no-reboot
