#!/bin/bash
# Check the >4 GiB test file size via FUSE.
LOOP=$(losetup -f --show /root/p10big.img)
mkdir -p /mnt/big
mount.exfat-fuse "$LOOP" /mnt/big >/dev/null 2>&1
stat -c '%s %n' /mnt/big/huge.bin
head -c 64 /mnt/big/huge.bin | od -c | head -2
echo "tail:"
tail -c 16 /mnt/big/huge.bin | od -c
fusermount -u /mnt/big
losetup -d "$LOOP"
