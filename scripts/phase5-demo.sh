#!/bin/bash
# NeutrinoOS Phase 5 demo: boots the OS in QEMU and walks through the
# shell, the utilities and the networking commands, printing the live
# transcript. Run from WSL after `build/p5-all.sh` has produced
# /root/run.img (it is also produced by the standard build + deploy).
set -u
cd /root/neutrino
pkill -9 qemu-system 2>/dev/null || true
for i in $(seq 1 20); do
  pgrep qemu-system >/dev/null 2>&1 || break
  sleep 0.5
done
pkill -9 -f qemu-system 2>/dev/null || true

cleanup() {
  pkill -9 -f qemu-system 2>/dev/null || true
  rm -f /root/qin
}
trap cleanup EXIT

if [ ! -f /root/run.img ]; then
  echo "demo: /root/run.img missing - run build/p5-all.sh first"
  exit 1
fi

echo "=== NeutrinoOS Phase 5 demo ==="
echo "(boots in QEMU; the shell drives itself through the demo tour)"
sleep 1
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd

# Fast boot for the demo: skip the legacy in-kernel self-tests (they
# JIT-saturate the runtime); the demo exercises the shell instead.
echo 1 > /tmp/skip-boot-tests
mcopy -o -i /root/run.img /tmp/skip-boot-tests ::/skip-boot-tests

rm -f /root/qin /root/q5demo.log
mkfifo /root/qin

( tail -f /root/qin | timeout 300 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0 -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/q5demo.log 2>&1 ) &

echo "waiting for shell..."
for i in $(seq 1 120); do
  if strings /root/q5demo.log 2>/dev/null | grep -q 'neutrinoos> '; then break; fi
  sleep 1
done
sleep 1

send() { echo "$1" > /root/qin; sleep "${2:-3}"; }

send 'echo === shell basics ==='
send 'pwd'
send 'ls'
send 'echo hello > /demo.txt'
send 'cat /demo.txt'
send 'echo world >> /demo.txt'
send 'wc -l < /demo.txt'
send 'ls /bin | wc -l' 4
send 'echo === background jobs ==='
send 'sleep 2 &'
send 'jobs'
send 'echo === system info ===' 2
send 'uname -a'
send 'uptime'
send 'date'
send 'free'
send 'df'
send 'echo === tab completion ==='
printf 'ec' > /root/qin; sleep 1
printf '\t' > /root/qin; sleep 4
printf 'tab-completion-output\n' > /root/qin; sleep 2
printf 'l' > /root/qin; sleep 1
printf '\t' > /root/qin; sleep 2
printf 's /bin | wc -l\n' > /root/qin; sleep 3
send 'echo === prompt customization ==='
send "export PS1='\\u@\\h:\\w\\\$ '" 2
send 'pwd'
send 'echo === networking ===' 2
send 'ping -c 1 127.0.0.1' 5
send 'ifconfig' 5
send 'echo === history ==='
send 'history' 3
send 'exit' 4

echo "=== DEMO TRANSCRIPT (tail) ==="
tr -d '\r' < /root/q5demo.log | tail -150
echo "=== DEMO END ==="
