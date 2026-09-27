#!/bin/bash
# Post-run verification of the VirtualBox test disks (FUSE + exfatprogs).
# Usage: p10-vbox-verify.sh [data-image] [blank-image]
set -u
OUT=/mnt/d/Projects/Code/NeutrinoOS/build/vbox-p10
DATA_IMG="${1:-$OUT/data.img}"
BLANK_IMG="${2:-$OUT/blank.img}"
PASS=0; FAIL=0
check() {
  if [ "$1" = "1" ]; then echo "PASS: $2"; PASS=$((PASS+1));
  else echo "FAIL: $2"; FAIL=$((FAIL+1)); fi
}

LOOP=$(losetup -f --show "$DATA_IMG")
mkdir -p /mnt/vboxd
if mount.exfat-fuse "$LOOP" /mnt/vboxd >/dev/null 2>&1; then
  check 1 "exfat-fuse mounts data.img after guest writes"
  if [ -f /mnt/vboxd/new.txt ]; then
    CONTENT=$(tr -d '\0' < /mnt/vboxd/new.txt)
    [ "$CONTENT" = "hello-vbox-guest" ] \
      && check 1 "guest-written new.txt readable in Linux" || { od -c /mnt/vboxd/new.txt | head -2; check 0 "guest-written new.txt readable in Linux"; }
  else
    check 0 "guest-written new.txt readable in Linux"
  fi
  [ -d /mnt/vboxd/dir1 ] && check 1 "guest-created dir1 visible in Linux" || check 0 "guest-created dir1 visible in Linux"
  [ -f /mnt/vboxd/renamed.txt ] && check 1 "renamed.txt visible in Linux" || check 0 "renamed.txt visible in Linux"
  if [ -f /mnt/vboxd/big.bin ]; then
    [ "$(stat -c%s /mnt/vboxd/big.bin)" = "4194304" ] \
      && check 1 "big.bin intact (4 MiB)" || check 0 "big.bin intact (4 MiB)"
  else
    check 0 "big.bin intact (4 MiB)"
  fi
  fusermount -u /mnt/vboxd
else
  check 0 "exfat-fuse mounts data.img after guest writes"
fi
losetup -d "$LOOP" 2>/dev/null

fsck.exfat "$DATA_IMG" > /tmp/vbox-fsck-data.log 2>&1
grep -q "clean" /tmp/vbox-fsck-data.log && check 1 "data.img fsck clean (exfatprogs)" || check 0 "data.img fsck clean (exfatprogs)"

LBL=$(/usr/sbin/exfatlabel "$BLANK_IMG" 2>/dev/null)
echo "$LBL" | grep -q "VBOXMADE" && check 1 "blank.img label VBOXMADE (mkexfat in guest)" || { echo "label: $LBL"; check 0 "blank.img label VBOXMADE (mkexfat in guest)"; }
fsck.exfat "$BLANK_IMG" > /tmp/vbox-fsck-blank.log 2>&1
grep -q "clean" /tmp/vbox-fsck-blank.log && check 1 "blank.img fsck clean (exfatprogs)" || check 0 "blank.img fsck clean (exfatprogs)"

echo
echo "=== VBOX-VERIFY: $PASS PASS / $FAIL FAIL ==="
[ $FAIL -eq 0 ] && echo "VBOXVERIFY: ALL-PASS" || echo "VBOXVERIFY: HAS-FAILURES"
exit $FAIL
