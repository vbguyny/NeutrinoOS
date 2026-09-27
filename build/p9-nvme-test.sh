#!/bin/bash
# Phase 9 Task 5: NVMe driver acceptance in QEMU.
# Creates a data disk with a signature at LBA0, boots QEMU with a QEMU
# NVMe controller, and checks the driver's IDENTIFY + read + write test.
set -u
cd /root/neutrino

pkill -9 -f 'p9nvme-qem[u]' 2>/dev/null || true
for i in $(seq 1 20); do pgrep -f 'p9nvme-qem[u]' >/dev/null 2>&1 || break; sleep 0.5; done

rm -f /root/nvme.img
python3 - <<'PYEOF'
data = bytearray(4 * 1024 * 1024)
sig = b"NEUTRINOS-OS\x00"
data[0:len(sig)] = sig
with open('/root/nvme.img', 'wb') as f:
    f.write(data)
print("nvme.img created")
PYEOF

echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests 2>/dev/null || true
# Enable the destructive write test in the driver.
echo 1 > /tmp/nvme-write-test
mcopy -o -i /root/run.img /tmp/nvme-write-test ::/nvme-write-test 2>/dev/null || true
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/p9nvmevars.fd

rm -f /root/p9nvme.in /root/p9nvme.log
mkfifo /root/p9nvme.in

( tail -f /root/p9nvme.in | timeout 120 qemu-system-x86_64 -name p9nvme-qemu \
  -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/p9nvmevars.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -drive id=nvmedisk,if=none,format=raw,file=/root/nvme.img \
  -device nvme,drive=nvmedisk,serial=NEUTRINO01 \
  -netdev user,id=n0,ipv6=on -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p9nvme.log 2>&1 ) &

ok=0
for i in $(seq 1 100); do
  if strings /root/p9nvme.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "prompt at ${i}s"; ok=1; break; fi
  sleep 1
done
if [ "$ok" != 1 ]; then echo "NO PROMPT"; tail -20 /root/p9nvme.log; pkill -9 -f 'p9nvme-qem[u]'; exit 1; fi

sleep 2
pkill -9 -f 'p9nvme-qem[u]' 2>/dev/null || true
tr -d '\r' < /root/p9nvme.log > /root/p9nvme.txt

echo "=== NVMe driver log ==="
grep -a '\[NVMe\]\|NVMe matched\|NVMe Bind\|NVMe driver' /root/p9nvme.txt | head -20
echo "=== verdict ==="
if grep -a 'signature OK' /root/p9nvme.txt > /dev/null && grep -a 'write test PASS' /root/p9nvme.txt > /dev/null; then
  echo "NVME: PASS"
else
  echo "NVME: FAIL"
fi
