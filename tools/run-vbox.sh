#!/bin/bash
# NeutrinoOS VirtualBox helper - converts the raw boot image to .vdi and
# prints the VBoxManage configuration for a UEFI VM whose serial port is
# logged to a file on the host.
#
# Requires VirtualBox 7.x (VBoxManage on PATH). On Windows the binary lives at:
#   "C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"
# See docs/BUILD-WINDOWS.md (VirtualBox section) for the full walkthrough.

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$SCRIPT_DIR"

ARCH="${ARCH:-x64}"
BUILD_DIR="build/${ARCH}"
IMG_FILE="${BUILD_DIR}/neutrinoos.img"
VDI_FILE="${BUILD_DIR}/neutrinoos.vdi"
VM_NAME="${VM_NAME:-NeutrinoOS}"
SERIAL_LOG="${SERIAL_LOG:-$SCRIPT_DIR/neutrinoos-serial.log}"

if ! command -v VBoxManage >/dev/null 2>&1; then
    echo "VBoxManage not found on PATH."
    echo ""
    echo "Install VirtualBox 7.x and either add VBoxManage to PATH or run the"
    echo "commands printed below manually with the full path to VBoxManage.exe."
    echo "See docs/BUILD-WINDOWS.md (VirtualBox section)."
    exit 1
fi

if [ ! -f "$IMG_FILE" ]; then
    echo "Error: boot image not found: $IMG_FILE"
    echo "Run 'make image' first"
    exit 1
fi

echo "Creating ${VDI_FILE} from ${IMG_FILE}..."
rm -f "$VDI_FILE"
VBoxManage convertfromraw "$IMG_FILE" "$VDI_FILE" --format VDI --variant Standard

echo ""
echo "Create and configure a UEFI VM with serial logging (adjust paths per host):"
echo ""
cat <<EOF
  VBoxManage createvm --name "$VM_NAME" --ostype "Other_64" --register
  VBoxManage modifyvm "$VM_NAME" --firmware efi --chipset ich9 --memory 2048 --cpus 2
  VBoxManage modifyvm "$VM_NAME" --graphicscontroller vmsvga
  VBoxManage storagectl "$VM_NAME" --name "SATA" --add sata --controller IntelAhci
  VBoxManage storageattach "$VM_NAME" --storagectl "SATA" --port 0 --device 0 \\
      --type hdd --medium "$VDI_FILE"
  VBoxManage modifyvm "$VM_NAME" --uart1 0x3F8 4 --uartmode1 file "$SERIAL_LOG"
  VBoxManage startvm "$VM_NAME" --type headless
EOF
echo ""
echo "Serial output will be written to: $SERIAL_LOG"
echo "Note: network (when enabled later) should be Intel PRO/1000 MT Desktop or virtio-net."
