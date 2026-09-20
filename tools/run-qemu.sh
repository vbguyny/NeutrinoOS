#!/bin/bash
# NeutrinoOS minimal QEMU launcher - serial console only, no graphics.
#
# Boots neutrinoos.img under OVMF with the serial port wired to stdio.
# This is the Phase 1 reference configuration (see docs/PHASE1-ACCEPTANCE.md):
#   - q35 machine, 2 GB RAM
#   - OVMF via pflash (CODE readonly + writable VARS copy)
#   - neutrinoos.img attached on virtio
#   - no display, no GOP, no framebuffer - the only console is COM1 -> stdio
#
# Override firmware location with:
#   OVMF_CODE=/path/to/OVMF_CODE.fd OVMF_VARS=/path/to/OVMF_VARS.fd make run-qemu

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$SCRIPT_DIR"

ARCH="${ARCH:-x64}"
BUILD_DIR="build/${ARCH}"
IMG_FILE="${BUILD_DIR}/neutrinoos.img"
TEST_DISK="${BUILD_DIR}/test.img"
SATA_DISK="${BUILD_DIR}/sata.img"

if [ ! -f "$IMG_FILE" ]; then
    echo "Error: boot image not found: $IMG_FILE"
    echo "Run 'make image' first"
    exit 1
fi

# Find OVMF firmware (override with OVMF_CODE / OVMF_VARS)
OVMF_CODE_PATHS=(
    "${OVMF_CODE:-}"
    "/usr/share/OVMF/OVMF_CODE_4M.fd"
    "/usr/share/OVMF/OVMF_CODE.fd"
    "/usr/share/edk2-ovmf/x64/OVMF_CODE.fd"
)
OVMF_VARS_PATHS=(
    "${OVMF_VARS:-}"
    "/usr/share/OVMF/OVMF_VARS_4M.fd"
    "/usr/share/OVMF/OVMF_VARS.fd"
    "/usr/share/edk2-ovmf/x64/OVMF_VARS.fd"
)

OVMF_CODE_FD=""
for path in "${OVMF_CODE_PATHS[@]}"; do
    if [ -n "$path" ] && [ -f "$path" ]; then
        OVMF_CODE_FD="$path"
        break
    fi
done

if [ -z "$OVMF_CODE_FD" ]; then
    echo "Error: OVMF_CODE.fd not found."
    echo "Install it (Ubuntu: 'sudo apt install ovmf') or set OVMF_CODE=/path/to/OVMF_CODE.fd"
    exit 1
fi

FIRMWARE_ARGS=(-drive "if=pflash,format=raw,readonly=on,file=$OVMF_CODE_FD")

OVMF_VARS_FD=""
for path in "${OVMF_VARS_PATHS[@]}"; do
    if [ -n "$path" ] && [ -f "$path" ]; then
        OVMF_VARS_FD="$path"
        break
    fi
done
if [ -n "$OVMF_VARS_FD" ]; then
    VARS_COPY="${BUILD_DIR}/OVMF_VARS.fd"
    cp "$OVMF_VARS_FD" "$VARS_COPY"
    FIRMWARE_ARGS+=(-drive "if=pflash,format=raw,file=$VARS_COPY")
fi

# Attach test/sata disks when present (created by ./build.sh).
# Not required for Phase 1 console bring-up.
DISK_ARGS=(-drive "file=$IMG_FILE,format=raw,if=virtio")
if [ -f "$TEST_DISK" ]; then
    DISK_ARGS+=(-drive "id=virtio-disk0,if=none,format=raw,file=$TEST_DISK")
    DISK_ARGS+=(-device "virtio-blk-pci,drive=virtio-disk0,disable-legacy=on")
fi
if [ -f "$SATA_DISK" ]; then
    DISK_ARGS+=(-drive "id=sata-disk0,if=none,format=raw,file=$SATA_DISK")
    DISK_ARGS+=(-device "ide-hd,drive=sata-disk0,bus=ide.2")
fi

echo "OVMF code:  $OVMF_CODE_FD"
if [ -n "$OVMF_VARS_FD" ]; then
    echo "OVMF vars:  $OVMF_VARS_FD (copied to $BUILD_DIR/OVMF_VARS.fd)"
fi
echo "Boot image: $IMG_FILE"
echo ""
echo "Serial console below (Ctrl+A, X to exit QEMU):"
echo "=============================================="

exec qemu-system-x86_64 \
    -machine q35 \
    -m 2G \
    "${FIRMWARE_ARGS[@]}" \
    "${DISK_ARGS[@]}" \
    -display none \
    -serial stdio \
    -no-reboot \
    -no-shutdown
