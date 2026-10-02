#!/bin/bash
# Temporary: prove the fixed kernel does NOT corrupt its own boot image.
# Boots the MASTER CLI image directly (no copy!), single-disk config like
# the user's run, twice, typing 'ls' each time; verifies disk LBA 10000
# (the old AHCI TestWrite target) is byte-identical before vs after.
set -u
cd /root/neutrino
pkill -9 -f qin-nc 2>/dev/null || true
pkill -9 -f qemu-system 2>/dev/null || true
sleep 1

IMG=build/x64/neutrinoos-cli.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-nc.fd

md5sum "$IMG"
dd if="$IMG" bs=512 skip=10000 count=1 of=/tmp/lba-before.bin 2>/dev/null
md5sum /tmp/lba-before.bin

boof() {
    rm -f /root/qin-nc /root/nc-qemu.out
    setsid bash -c "tail -f /root/qin-nc | qemu-system-x86_64 -machine q35 -m 2G -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-nc.fd \
  -drive format=raw,file=$IMG \
  -netdev user,id=n0,hostfwd=tcp::2222-:22 -device virtio-net-pci,netdev=n0 \
  -vga std -display none -serial stdio -no-reboot -no-shutdown" < /dev/null > /root/nc-qemu.out 2>&1 &
    for i in $(seq 1 90); do
        if grep -q 'root-/>' /root/nc-qemu.out 2>/dev/null; then break; fi
        sleep 1
    done
    sleep 2
    printf 'ls\n' >> /root/qin-nc
    sleep 4
    printf 'uname\n' >> /root/qin-nc
    sleep 3
    pkill -9 -f qemu-system 2>/dev/null || true
    sleep 1
    echo "--- boot $1 tail:"
    tail -c 600 /root/nc-qemu.out | tr -d '\r'
}

boof first

boof second

echo
echo "=== LBA 10000 after two direct boots ==="
dd if="$IMG" bs=512 skip=10000 count=1 of=/tmp/lba-after.bin 2>/dev/null
md5sum /tmp/lba-before.bin /tmp/lba-after.bin
if cmp -s /tmp/lba-before.bin /tmp/lba-after.bin; then
    echo "RESULT: LBA 10000 UNCHANGED - image is non-destructive (FIXED)"
else
    echo "RESULT: LBA 10000 CHANGED - still corrupting!"
fi

echo
echo "=== errors / skip lines in last log ==="
grep -a -n 'PInvoke not found\|Unknown opcode\|Tier0JIT. ERROR\|\[run\] error\|TestWrite skipped\|TestWrite returned\|\[AhciIO\]' /root/nc-qemu.out | head -20
echo "(end)"
