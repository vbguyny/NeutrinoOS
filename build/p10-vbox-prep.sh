#!/bin/bash
# Phase 10 VirtualBox acceptance prep: fresh boot image (kernel + the
# deployed utility suite + skip-boot-tests marker) and the test disks,
# written to the Windows side (build/vbox-p10) for VBoxManage.
set -u
SRC=/mnt/d/Projects/Code/NeutrinoOS
OUT=$SRC/build/vbox-p10
HOSTDIR=$SRC/tests/exfat-host
H=$HOSTDIR/bin/Debug/net10.0/exfat-host

echo "=== refreshing run.img (kernel + utilities) ==="
bash "$SRC/build/p5-deploy.sh" >/dev/null 2>&1
[ -f /root/run.img ] || { echo "FAIL: /root/run.img missing"; exit 1; }

mkdir -p "$OUT"
cp -f /root/run.img "$OUT/boot.img"
echo 1 > /tmp/skip-boot-tests
export MTOOLS_SKIP_CHECK=1
exec < /dev/null
mcopy -o -i "$OUT/boot.img" /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true

echo "=== building host harness ==="
cd "$HOSTDIR" || exit 1
dotnet build -v q --nologo 2>&1 | grep -E 'error' | head -3

echo "=== exFAT data disk (our formatter) ==="
rm -f "$OUT/data.img"
"$H" "$OUT/data.img" format 64 P10VBOX | tail -1
"$H" "$OUT/data.img" write /linux.txt "from Linux, read by NeutrinoOS"
"$H" "$OUT/data.img" mkdir /docs
"$H" "$OUT/data.img" big /big.bin 4 | tail -1
fsck.exfat "$OUT/data.img" 2>&1 | tail -1

echo "=== blank disk (in-guest mkexfat target) ==="
rm -f "$OUT/blank.img"
dd if=/dev/zero of="$OUT/blank.img" bs=1M count=32 status=none

# VBox needs writable files this size; make sure they exist with sizes.
ls -la "$OUT"
echo "VBOX-PREP: OK"
