#!/bin/bash
# Phase 7: benchmark harness - boots the guest with a NIC, deploys the
# benchmark apps into /bin and runs the suite over the serial console.
#
#   bench_alloc      SOH/LOH allocation throughput
#   bench_file       FAT32 write/read throughput (64 KB / 256 KB / 1 MB sweep)
#   bench_jit        method-heavy workload (jitstats deltas captured)
#   bench_loopback   kernel-loopback TCP throughput (4 MB)
#   netstat -s       protocol/byte counters
#   cat /dev/netstats, /dev/random | wc -c, gcstats
set -u
cd /root/neutrino
exec < /dev/null
export MTOOLS_SKIP_CHECK=1
echo "[$(date +%T)] p7-bench start"
pkill -9 qemu-system 2>/dev/null || true
sleep 1

cleanup() {
  pkill -9 -f qemu-system 2>/dev/null || true
  rm -f /root/qin
}
trap cleanup EXIT

echo "[$(date +%T)] deploying image..."
bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-deploy.sh > /root/p7bench-deploy.log 2>&1

echo "[$(date +%T)] installing benchmark apps into ::/bin..."
for f in /root/phase7bin/*.dll; do
  [ -f "$f" ] || continue
  name=$(basename "$f")
  case "$name" in ProtonOS.DDK.dll) continue;; esac
  timeout -s KILL 20 mcopy -o -i /root/run.img "$f" "::/bin/$name" || echo "copy failed: $name"
done

sleep 1
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin /root/p7bench.log
mkfifo /root/qin

echo "[$(date +%T)] launching QEMU..."
( tail -f /root/qin | timeout 300 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0 -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p7bench.log 2>&1 ) &

BOOT_START=$(date +%s)
for i in $(seq 1 150); do
  if strings /root/p7bench.log 2>/dev/null | grep -q 'neutrinoos> '; then
    echo "prompt at $(( $(date +%s) - BOOT_START ))s"
    break
  fi
  sleep 1
done
sleep 2

send() {
  timeout 5 bash -c "printf '%s\n' '$1' > /root/qin" 2>/dev/null || echo "!! guest input fifo blocked"
  sleep "$2"
}

send "dhcp" 6
send "jitstats reset" 2
send "bench_alloc" 8
send "bench_file" 12
send "gcstats" 3
send "jitstats" 4
send "bench_jit" 10
send "jitstats" 4
send "bench_loopback" 35
send "netstat -s" 4
send "cat /dev/netstats" 4
send "cat /dev/random | wc -c" 6

sleep 1
echo "===== captured command output ====="
strings /root/p7bench.log | awk '/neutrinoos> dhcp/{f=1} f' | grep -avE '^\[JIT\] Compile|^\[AsmLoader\]|^\[AotMemberRef\]|^\[LazyJIT\]|^\[KorlibMethodDef\]|^\[NetExec\]' | head -220

echo "===== benchmark result lines ====="
strings /root/p7bench.log | grep -aE '\[bench\]' | tail -12

echo "===== SYSTEM HALTED count ====="
strings /root/p7bench.log | grep -sc 'SYSTEM HALTED' || true
echo "[$(date +%T)] p7-bench done"
