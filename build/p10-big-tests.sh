#!/bin/bash
# Phase 10 acceptance stragglers: >10K-entry directory + >4 GiB file.
set -u
H=/mnt/d/Projects/Code/NeutrinoOS/tests/exfat-host/bin/Debug/net10.0/exfat-host
IMG=/root/p10big.img

echo "=== 10K-entry directory ==="
rm -f "$IMG"
$H "$IMG" format 64 BIGLABEL | tail -1
$H "$IMG" mkdir /big | tail -1
$H "$IMG" fill /big 10000 | tail -2
echo "--- listing count:"
$H "$IMG" list /big | grep -c '\.txt' || true
fsck.exfat "$IMG" 2>&1 | tail -1

echo "=== 4.5 GiB file ==="
rm -f "$IMG"
$H "$IMG" format 6000 BIG4G | tail -1
$H "$IMG" big /huge.bin 4608 | tail -3
fsck.exfat "$IMG" 2>&1 | tail -1
