#!/bin/bash
# Phase 7: rebuild DDK, restart the serve VM, and re-probe latency.
set -u
pkill -9 qemu-system 2>/dev/null || true
sleep 1
bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-apps-build.sh > /root/p7ddkq2.log 2>&1
echo "APPSDONE errors=$(grep -acE 'error CS' /root/p7ddkq2.log || true)"
nohup bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-qemu-serve.sh > /root/p6serve-run2.log 2>&1 &
disown
touch /tmp/p7serve.launch
echo "LAUNCHED"
for i in $(seq 1 120); do
  if [ -f /root/p6serve.log ] && [ /root/p6serve.log -nt /tmp/p7serve.launch ] \
     && strings /root/p6serve.log 2>/dev/null | grep -q 'Listening on port 443'; then
    echo "servers ready at ${i}s"
    break
  fi
  sleep 1
done
sleep 2
bash /mnt/d/Projects/Code/NeutrinoOS/build/p7-latency.sh after-quiet
