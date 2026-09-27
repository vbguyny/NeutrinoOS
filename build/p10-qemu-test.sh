#!/bin/bash
# Phase 10 QEMU acceptance - full in-VM story:
#  1. second AHCI drive (hdb) carries an exFAT volume (our formatter):
#     mount via utilities, read a Linux-written file, write/mkdir/mv,
#     df -T, umount, then fsck.exfat + exfatlabel read-back.
#  2. third AHCI drive (hdc) is blank: mkexfat formats it in-guest and
#     fsck.exfat verifies the result.
#  3. a USB stick (qemu-xhci + usb-storage) with an exFAT volume is
#     auto-mounted at /mnt/usb/sda by the DDK AutoMount service; the
#     guest reads and writes it.
# Everything is verified back in Linux with exfat-fuse and exfatprogs.
set -u
SRC=/mnt/d/Projects/Code/NeutrinoOS
WORK=/root/p10q
IMG=$WORK/exfat.img          # hdb: data disk (formatted by our formatter)
FMT=$WORK/blank.img          # hdc: mkexfat target (zeroed)
STICK=$WORK/stick.img        # USB stick (our formatter)
LOG=/root/p10qemu.log

pkill -9 qemu-system 2>/dev/null || true
sleep 1
rm -rf "$WORK"; mkdir -p "$WORK"

PASS=0; FAIL=0
check() {
  if [ "$1" = "1" ]; then echo "PASS: $2"; PASS=$((PASS+1));
  else echo "FAIL: $2"; FAIL=$((FAIL+1)); fi
}

echo "=== building host harness ==="
cd "$SRC/tests/exfat-host" || exit 1
dotnet build -v q --nologo 2>&1 | grep -E 'error|Build succeeded' | head -5
HOST="$SRC/tests/exfat-host/bin/Debug/net10.0/exfat-host"
[ -x "$HOST" ] || { echo "FAIL: host harness missing"; exit 1; }

echo "=== creating images ==="
"$HOST" "$IMG" format 64 P10QEMU || exit 1
"$HOST" "$IMG" write /linux.txt "from Linux, read by NeutrinoOS"
"$HOST" "$IMG" mkdir /docs
"$HOST" "$IMG" big /big.bin 4
fsck.exfat "$IMG" > "$WORK/fsck-pre.log" 2>&1
grep -q "clean" "$WORK/fsck-pre.log" && check 1 "pre-boot fsck clean (hdb, our formatter)" || check 0 "pre-boot fsck clean (hdb, our formatter)"

dd if=/dev/zero of="$FMT" bs=1M count=24 status=none

"$HOST" "$STICK" format 24 USBSTICK || exit 1
"$HOST" "$STICK" write /usb.txt "stick file from Linux"

echo "=== booting QEMU (hdb exFAT + hdc blank + USB stick) ==="
cp -f /root/run.img "$WORK/run.img" 2>/dev/null || cp -f /root/neutrino/build/x64/neutrinoos.img "$WORK/run.img"
echo 1 > /tmp/skip-boot-tests
export MTOOLS_SKIP_CHECK=1
mcopy -o -i "$WORK/run.img" /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /root/neutrino/build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin "$LOG"
mkfifo /root/qin

( tail -f /root/qin | timeout 300 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/root/neutrino/build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file="$WORK/run.img" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -drive id=exfatdisk,if=none,format=raw,file="$IMG" \
  -device ide-hd,drive=exfatdisk,bus=ide.1 \
  -drive id=fmtblank,if=none,format=raw,file="$FMT" \
  -device ide-hd,drive=fmtblank,bus=ide.2 \
  -device qemu-xhci \
  -drive id=usbstick,if=none,format=raw,file="$STICK" \
  -device usb-storage,drive=usbstick \
  -display none -serial stdio -no-reboot -no-shutdown > "$LOG" 2>&1 ) &

echo "waiting for shell..."
for i in $(seq 1 120); do
  if strings "$LOG" 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; break; fi
  sleep 1
done
sleep 2

cmd() { echo "$1" > /root/qin; sleep "${2:-3}"; }

# ---- hdb: mount / I-O / df / umount / fsck / label ----
cmd "mount -t exfat /dev/hdb /mnt/test" 3
cmd "ls /mnt/test" 3
cmd "cat /mnt/test/linux.txt" 3
cmd "echo hello-from-neutrinoos > /mnt/test/new.txt" 3
cmd "mkdir /mnt/test/dir1" 3
cmd "mv /mnt/test/linux.txt /mnt/test/renamed.txt" 3
cmd "df -T" 3
cmd "umount /mnt/test" 3
cmd "fsck.exfat /dev/hdb" 4
cmd "exfatlabel /dev/hdb" 2

# ---- hdc: mkexfat formats the blank disk in-guest ----
cmd "mkexfat -L MADE72 /dev/hdc" 5
cmd "fsck.exfat /dev/hdc" 4

# ---- USB stick: wait for automount, then use the volume ----
echo "waiting for automount..."
for i in $(seq 1 30); do
  if strings "$LOG" 2>/dev/null | grep -q '\[automount\] /dev/sda mounted at /mnt/usb/sda'; then echo "automounted at ${i}s"; break; fi
  sleep 1
done
cmd "ls /mnt/usb/sda" 4
cmd "cat /mnt/usb/sda/usb.txt" 4
cmd "echo guest-on-usb > /mnt/usb/sda/guest.txt" 4
cmd "df -T" 3
cmd "exit" 2

pkill -9 qemu-system 2>/dev/null || true
sleep 1

echo "=== checking guest transcript ==="
T=$(strings "$LOG")
echo "$T" | grep -q 'mounted hdb (exFAT, label "P10QEMU"' && check 1 "mount reports exFAT label" || check 0 "mount reports exFAT label"
echo "$T" | grep -q "read by NeutrinoOS" && check 1 "read Linux-written file" || check 0 "read Linux-written file"
echo "$T" | grep -q "renamed.txt" && check 1 "mv renamed file (listing)" || check 0 "mv renamed file (listing)"
echo "$T" | grep -q "dir1" && check 1 "mkdir created dir1" || check 0 "mkdir created dir1"
echo "$T" | grep -q "P10QEMU" && echo "$T" | grep -q "exfat" && check 1 "df -T shows exfat mount" || check 0 "df -T shows exfat mount"
echo "$T" | grep -q "unmounted /mnt/test" && check 1 "umount succeeded" || check 0 "umount succeeded"
echo "$T" | grep -q "fsck.exfat: checking hdb" && check 1 "fsck.exfat ran on hdb" || check 0 "fsck.exfat ran on hdb"
echo "$T" | grep -q "fsck.exfat: clean" && check 1 "in-guest fsck.exfat /dev/hdb clean" || check 0 "in-guest fsck.exfat /dev/hdb clean"
echo "$T" | grep -q "\[automount\] /dev/sda mounted at /mnt/usb/sda" && check 1 "USB automounted at /mnt/usb/sda" || check 0 "USB automounted at /mnt/usb/sda"
echo "$T" | grep -q "USBSTICK" && check 1 "automount shows volume label" || check 0 "automount shows volume label"
echo "$T" | grep -qE 'tick file from Linux|stick file from Linux' && check 1 "read USB file" || check 0 "read USB file"
echo "$T" | grep -q "formatted hdc (exFAT" && check 1 "mkexfat formatted hdc in-guest" || check 0 "mkexfat formatted hdc in-guest"

echo "=== verifying images back in Linux (FUSE + exfatprogs) ==="
LOOP=$(losetup -f --show "$IMG")
mkdir -p /mnt/p10q
if mount.exfat-fuse "$LOOP" /mnt/p10q > "$WORK/fuse.log" 2>&1; then
  check 1 "exfat-fuse mounts the image after guest writes"
  if [ -f /mnt/p10q/new.txt ]; then
    CONTENT=$(tr -d '\0' < /mnt/p10q/new.txt)
    [ "$CONTENT" = "hello-from-neutrinoos" ] \
      && check 1 "guest-written new.txt readable in Linux" || { od -c /mnt/p10q/new.txt | head -2; check 0 "guest-written new.txt readable in Linux"; }
  else
    check 0 "guest-written new.txt readable in Linux"
  fi
  [ -d /mnt/p10q/dir1 ] && check 1 "guest-created dir1 visible in Linux" || check 0 "guest-created dir1 visible in Linux"
  [ -f /mnt/p10q/renamed.txt ] && check 1 "renamed.txt visible in Linux" || check 0 "renamed.txt visible in Linux"
  [ -f /mnt/p10q/big.bin ] && [ "$(stat -c%s /mnt/p10q/big.bin)" = "4194304" ] \
    && check 1 "big.bin intact (4 MiB)" || check 0 "big.bin intact (4 MiB)"
  fusermount -u /mnt/p10q
else
  check 0 "exfat-fuse mounts the image after guest writes"
fi
losetup -d "$LOOP" 2>/dev/null

fsck.exfat "$IMG" > "$WORK/fsck-post.log" 2>&1
grep -q "clean" "$WORK/fsck-post.log" && check 1 "post-boot fsck clean (hdb)" || check 0 "post-boot fsck clean (hdb)"

# hdc: labelled + formatted in-guest
LBL=$(/usr/sbin/exfatlabel "$FMT" 2>/dev/null)
echo "$LBL" | grep -q "MADE72" && check 1 "hdc label MADE72 (formatted in-guest)" || { echo "label: $LBL"; check 0 "hdc label MADE72 (formatted in-guest)"; }
fsck.exfat "$FMT" > "$WORK/fsck-fmt.log" 2>&1
grep -q "clean" "$WORK/fsck-fmt.log" && check 1 "hdc fsck clean (mkexfat output)" || check 0 "hdc fsck clean (mkexfat output)"

# USB stick: guest file visible
LOOP=$(losetup -f --show "$STICK")
mkdir -p /mnt/p10usb
if mount.exfat-fuse "$LOOP" /mnt/p10usb > "$WORK/fuse-usb.log" 2>&1; then
  check 1 "exfat-fuse mounts the USB stick image"
  if [ -f /mnt/p10usb/guest.txt ]; then
    CONTENT=$(tr -d '\0' < /mnt/p10usb/guest.txt)
    [ "$CONTENT" = "guest-on-usb" ] \
      && check 1 "guest-written USB file readable in Linux" || { od -c /mnt/p10usb/guest.txt | head -2; check 0 "guest-written USB file readable in Linux"; }
  else
    check 0 "guest-written USB file readable in Linux"
  fi
  if [ -f /mnt/p10usb/usb.txt ]; then
    CONTENT=$(tr -d '\0' < /mnt/p10usb/usb.txt)
    [ "$CONTENT" = "stick file from Linux" ] \
      && check 1 "usb.txt intact (content)" || check 0 "usb.txt intact (content)"
  else
    check 0 "usb.txt intact (content)"
  fi
  fusermount -u /mnt/p10usb
else
  check 0 "exfat-fuse mounts the USB stick image"
fi
losetup -d "$LOOP" 2>/dev/null
fsck.exfat "$STICK" > "$WORK/fsck-usb.log" 2>&1
grep -q "clean" "$WORK/fsck-usb.log" && check 1 "USB stick fsck clean" || check 0 "USB stick fsck clean"

echo
echo "=== p10 QEMU summary: $PASS PASS / $FAIL FAIL ==="
[ $FAIL -eq 0 ] && echo "P10QEMU: ALL-PASS" || echo "P10QEMU: HAS-FAILURES"
exit $FAIL
