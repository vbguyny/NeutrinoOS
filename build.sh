#!/bin/bash
# NeutrinoOS build script (native, no Docker).
#
# Incremental by default: only what changed is recompiled, and the boot image
# is only reassembled when one of its contents changed. A no-op rebuild takes a
# couple of seconds; a kernel-source change costs one bflat run (~10 s).
#
# Usage: ./build.sh [options]
#   --trace        compile in the runtime JIT/AOT trace prints
#                  (see src/kernel/Runtime/JitTrace.cs)
#   --clean        wipe build/<arch> (and korlib obj/bin) first
#   --il           regenerate the .il disassemblies (slow, debug only)
#   -j N, --jobs N parallelism for make (default: number of CPUs)

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

CLEAN=0
GEN_IL=0
JOBS=""
while [ $# -gt 0 ]; do
    case "$1" in
        --trace) export TRACE=1 ;;
        --clean) CLEAN=1 ;;
        --il) GEN_IL=1 ;;
        -j|--jobs)
            [ $# -ge 2 ] || { echo "build.sh: $1 needs a value" >&2; exit 2; }
            JOBS="$2"; shift ;;
        -j*) JOBS="${1#-j}" ;;
        *) echo "build.sh: unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -z "$JOBS" ]; then
    JOBS="$( (command -v nproc >/dev/null 2>&1 && nproc) || echo 4)"
fi

ARCH="${ARCH:-x64}"
BUILD_DIR="build/${ARCH}"
TEST_DISK="${BUILD_DIR}/test.img"
SATA_DISK="${BUILD_DIR}/sata.img"
TESTDATA_DIR="$SCRIPT_DIR/testdata"

start=$(date +%s)

# Kill any running QEMU instances (they keep the image files open)
"$SCRIPT_DIR/kill.sh" 2>/dev/null || true

if [ "$CLEAN" = "1" ]; then
    # Clean build directory
    "$SCRIPT_DIR/clean.sh"
fi

# Stamp this build: increments the build number (.buildnum) and regenerates
# src/kernel/Generated/NeutrinoVersion.cs -> version 0.1.x.
# Skip with NO_BUMP=1 when iterating without wanting a new version.
if [ "${NO_BUMP:-0}" != "1" ]; then
    bash "$SCRIPT_DIR/version-bump.sh"
fi

# Build (make is incremental: only changed sources are recompiled, and the
# boot image is only reassembled when one of its contents is newer)
make -j"$JOBS" image

t1=$(date +%s)
echo "[build] compile+image: $((t1-start)) s"

# Generate IL disassembly for all runtime assemblies (debug aid, opt-in: the
# .il files are large and cost a dotnet process each).
if [ "$GEN_IL" = "1" ]; then
    echo "Generating IL disassembly..."
    assemblies="JITTest TestSupport NeutrinoOS.DDK NeutrinoOS.Drivers.Virtio NeutrinoOS.Drivers.VirtioBlk NeutrinoOS.Drivers.Fat NeutrinoOS.Drivers.Ahci NeutrinoOS.Drivers.Ext2 NeutrinoOS.Drivers.VirtioNet NeutrinoOS.Drivers.Test NeutrinoOS.Net AppTest HelloApp ArgsApp"
    pids=""
    for a in $assemblies; do
        [ -f "$BUILD_DIR/$a.dll" ] || continue
        dotnet ildasm "$BUILD_DIR/$a.dll" -o "$BUILD_DIR/$a.il" >/dev/null 2>&1 &
        pids="$pids $!"
    done
    for p in $pids; do wait "$p" 2>/dev/null || true; done
    echo "IL disassembly complete"
fi

# Create test disk image for FAT filesystem testing (only when missing, or
# after --clean wiped the build directory).
if [ "$CLEAN" = "1" ] || [ ! -f "$TEST_DISK" ]; then
echo "Creating test disk image..."
# Create 64MB FAT32 disk image (FAT32 needs at least ~33MB)
dd if=/dev/zero of="$TEST_DISK" bs=1M count=64 status=none
mformat -i "$TEST_DISK" -F -v TESTDISK ::

# Copy test files from testdata/ directory
if [ -d "$TESTDATA_DIR" ]; then
    echo "Copying test files from testdata/..."
    # Copy root level files
    for file in "$TESTDATA_DIR"/*; do
        if [ -f "$file" ]; then
            mcopy -i "$TEST_DISK" "$file" "::/$(basename "$file")"
        fi
    done
    # Copy directories recursively
    for dir in "$TESTDATA_DIR"/*/; do
        if [ -d "$dir" ]; then
            dirname=$(basename "$dir")
            mmd -i "$TEST_DISK" "::/$dirname" 2>/dev/null || true
            for file in "$dir"*; do
                if [ -f "$file" ]; then
                    mcopy -i "$TEST_DISK" "$file" "::/$dirname/$(basename "$file")"
                fi
            done
        fi
    done
fi
echo "Test disk created: $TEST_DISK"
fi

# Create root filesystem image (EXT2)
# This will be mounted as / after boot (skipped when it already exists).
if [ "$CLEAN" = "1" ] || [ ! -f "$SATA_DISK" ]; then
echo "Creating root filesystem image (EXT2)..."
ROOTFS_DIR="$SCRIPT_DIR/rootfs"
ROOTFS_STAGING="${BUILD_DIR}/rootfs_staging"

# Create staging directory with rootfs structure
rm -rf "$ROOTFS_STAGING"
mkdir -p "$ROOTFS_STAGING"

# Copy rootfs template
if [ -d "$ROOTFS_DIR" ]; then
    cp -r "$ROOTFS_DIR"/* "$ROOTFS_STAGING"/
fi

# Ensure standard directories exist
mkdir -p "$ROOTFS_STAGING/drivers"
mkdir -p "$ROOTFS_STAGING/etc"
mkdir -p "$ROOTFS_STAGING/system"
mkdir -p "$ROOTFS_STAGING/tmp"

# Copy test driver to /drivers (dynamically loaded after root mount)
if [ -f "${BUILD_DIR}/NeutrinoOS.Drivers.Test.dll" ]; then
    cp "${BUILD_DIR}/NeutrinoOS.Drivers.Test.dll" "$ROOTFS_STAGING/drivers/"
    echo "Copied test driver to rootfs/drivers/"
fi

# Copy testdata for VFS testing (optional, can be removed later)
if [ -d "$TESTDATA_DIR" ]; then
    cp -r "$TESTDATA_DIR"/* "$ROOTFS_STAGING"/
fi

# Create the ext2 filesystem with the staging directory contents
dd if=/dev/zero of="$SATA_DISK" bs=1M count=64 status=none
mkfs.ext2 -F -q -d "$ROOTFS_STAGING" "$SATA_DISK"

# Clean up staging
rm -rf "$ROOTFS_STAGING"

echo "Root filesystem created: $SATA_DISK (EXT2)"
echo "  /drivers  - additional drivers (loaded after root mount)"
echo "  /etc      - configuration files"
echo "  /system   - system files"
fi

end=$(date +%s)
echo "[build] total: $((end-start)) s"
