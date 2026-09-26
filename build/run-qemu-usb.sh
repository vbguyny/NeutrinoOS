#!/bin/bash
# NeutrinoOS QEMU launcher with USB 3.x (xHCI) - Phase 9 Task 1.
#
# Boots neutrinoos.img under OVMF with:
#   - q35 machine, 2 GB RAM
#   - a qemu-xhci controller (USB 3.x)
#   - a USB keyboard (usb-kbd) and USB mouse (usb-mouse)
#   - a USB mass storage stick (usb-storage) backed by
#     build/x64/usb-stick.img (created with a FAT filesystem and a
#     README when missing)
#
# Serial console on stdio; type in the serial console like the other
# run targets. Use the QEMU monitor (Ctrl+A, C) for hot-plug tests:
#   device_add usb-kbd,id=kb2     (plug)
#   device_del kb2                (unplug)
#
# Override firmware location with OVMF_CODE / OVMF_VARS as in
# tools/run-qemu.sh.

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$SCRIPT_DIR"

ARCH="${ARCH:-x64}"
BUILD_DIR="build/${ARCH}"
IMG_FILE="${BUILD_DIR}/neutrinoos.img"
USB_STICK="${BUILD_DIR}/usb-stick.img"

if [ ! -f "$IMG_FILE" ]; then
    echo "Error: boot image not found: $IMG_FILE"
    echo "Run 'make image' first"
    exit 1
fi

# Create the USB stick (16 MB FAT16) with a README when missing.
if [ ! -f "$USB_STICK" ]; then
    echo "Creating USB stick image: $USB_STICK"
    dd if=/dev/zero of="$USB_STICK" bs=1M count=16 status=none
    mformat -i "$USB_STICK" -F -v NEUTRINOUS || true
    printf 'NeutrinoOS USB mass storage test volume.\r\n' > /tmp/usb-readme.txt
    mcopy -i "$USB_STICK" /tmp/usb-readme.txt ::/README.TXT || true
fi

OVMF_CODE_PATHS=(
    "${OVMF_CODE:-}"
    "/usr/share/OVMF/OVMF_CODE_4M.fd"
    "/usr/share/OVMF/OVMF_CODE.fd"
)
OVMF_CODE_FD=""
for path in "${OVMF_CODE_PATHS[@]}"; do
    if [ -n "$path" ] && [ -f "$path" ]; then
        OVMF_CODE_FD="$path"
        break
    fi
done
if [ -z "$OVMF_CODE_FD" ]; then
    echo "Error: OVMF_CODE.fd not found (set OVMF_CODE=...)"
    exit 1
fi

OVMF_VARS_PATHS=(
    "${OVMF_VARS:-}"
    "/usr/share/OVMF/OVMF_VARS_4M.fd"
    "/usr/share/OVMF/OVMF_VARS.fd"
)
OVMF_VARS_FD=""
for path in "${OVMF_VARS_PATHS[@]}"; do
    if [ -n "$path" ] && [ -f "$path" ]; then
        OVMF_VARS_FD="$path"
        break
    fi
done
VARS_COPY="${BUILD_DIR}/OVMF_VARS-usb.fd"
cp "$OVMF_VARS_FD" "$VARS_COPY"

echo "OVMF code:  $OVMF_CODE_FD"
echo "Boot image: $IMG_FILE"
echo "USB stick:  $USB_STICK"
echo ""
echo "USB: qemu-xhci + usb-kbd + usb-mouse + usb-storage"
echo "Serial console below (Ctrl+A, X to exit QEMU):"
echo "=============================================="

exec qemu-system-x86_64 \
    -machine q35 \
    -m 2G \
    -drive "if=pflash,format=raw,readonly=on,file=$OVMF_CODE_FD" \
    -drive "if=pflash,format=raw,file=$VARS_COPY" \
    -drive "id=bootdisk,if=none,format=raw,file=$IMG_FILE" \
    -device "ide-hd,drive=bootdisk,bus=ide.0" \
    -device qemu-xhci \
    -device usb-kbd \
    -device usb-mouse \
    -drive "id=usbstick,if=none,format=raw,file=$USB_STICK" \
    -device "usb-storage,drive=usbstick" \
    -display none \
    -serial stdio \
    -no-reboot \
    -no-shutdown
