#!/bin/bash
# Boot the dev image N times and count halts / Test-56 outcomes (crash-rate
# measurement for the per-thread kernel-stack fix). usage: p7-bootloop.sh [N]
set -u
cd /root/neutrino
N=${1:-8}
halts=0
passes=0
other=0

for i in $(seq 1 $N); do
  pkill -9 qemu-system 2>/dev/null || true
  sleep 1
  cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
  rm -f /root/bootloop.log
  timeout -s KILL 75 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
    -drive id=bootdisk,if=none,format=raw,file=build/x64/neutrinoos.img \
    -device ide-hd,drive=bootdisk,bus=ide.0 \
    -display none -serial file:/root/bootloop.log -no-reboot -no-shutdown \
    > /dev/null 2>&1 &
  QPID=$!

  # Wait for shell prompt or halt, up to 70s
  result="timeout"
  for t in $(seq 1 70); do
    if strings /root/bootloop.log 2>/dev/null | grep -q 'SYSTEM HALTED'; then result="halt"; break; fi
    if strings /root/bootloop.log 2>/dev/null | grep -q 'neutrinoos> '; then result="pass"; break; fi
    sleep 1
  done

  case "$result" in
    halt) halts=$((halts+1));;
    pass) passes=$((passes+1));;
    *) other=$((other+1));;
  esac
  echo "[$(date +%T)] boot $i/$N: $result"
  if [ "$result" = "halt" ]; then
    strings /root/bootloop.log | tail -6
  fi
  kill -9 $QPID 2>/dev/null || true
  sleep 1
done

echo "=== summary: $N boots -> pass=$passes halt=$halts other=$other"
pkill -9 qemu-system 2>/dev/null || true
