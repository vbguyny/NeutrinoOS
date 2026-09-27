#!/bin/bash
# >4 GiB file acceptance: format 6 GB volume, write 4.5 GiB, verify.
set -u
HOSTDIR=/mnt/d/Projects/Code/NeutrinoOS/tests/exfat-host
H=$HOSTDIR/bin/Debug/net10.0/exfat-host
IMG=/root/p10big.img

cd "$HOSTDIR" && dotnet build -v q --nologo 2>&1 | grep -E 'error' | head -3

rm -f "$IMG"
"$H" "$IMG" format 6000 BIG4G | tail -1
"$H" "$IMG" big /huge.bin 4608 2>&1 | tail -8
echo "--- fsck:"
fsck.exfat "$IMG" 2>&1 | tail -1
echo "--- stat via FUSE:"
LOOP=$(losetup -f --show "$IMG")
mkdir -p /mnt/big
mount.exfat-fuse "$LOOP" /mnt/big >/dev/null 2>&1
stat -c '%s %n' /mnt/big/huge.bin
fusermount -u /mnt/big
losetup -d "$LOOP"
