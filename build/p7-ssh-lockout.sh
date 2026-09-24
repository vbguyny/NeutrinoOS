#!/bin/bash
# Phase 7: sshd brute-force lockout + audit log acceptance probe.
#
# 1. Deploy the serve image (test fixture enables password auth and sets
#    BanThreshold=3 / BanSeconds=10 / MaxAuthAttempts=3).
# 2. Boot QEMU (hostfwd 2222->22), start dhcp + sshd from the console.
# 3. Key auth works.  Three wrong-password attempts -> IP banned:
#    key auth from the same IP is now refused at accept time.
# 4. /var/log/auth.log (read over the serial console) contains the
#    "auth fail" lines and the "banning" line.
# 5. After the 10s ban expires, key auth works again.
#
# Prints PASS/FAIL lines; exit code 0 when every check passed.
set -u
cd /root/neutrino
exec < /dev/null
export MTOOLS_SKIP_CHECK=1
echo "[$(date +%T)] p7-ssh-lockout start"
pkill -9 qemu-system 2>/dev/null || true
sleep 1

FAIL=0
ok()   { echo "PASS $1"; }
bad()  { echo "FAIL $1"; FAIL=1; }

cleanup() {
  pkill -9 -f qemu-system 2>/dev/null || true
  rm -f /root/qin
}
trap cleanup EXIT

if [ ! -f /root/p6key ]; then
  ssh-keygen -t ed25519 -N "" -f /root/p6key -q
fi
cp /root/p6key.pub /mnt/d/Projects/Code/NeutrinoOS/build/authorized_keys.pub

echo "[$(date +%T)] deploying image..."
bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-deploy.sh > /root/p7lock-deploy.log 2>&1
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-image-extras.sh >> /root/p7lock-deploy.log 2>&1
sleep 1
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin /root/p7lock.log
mkfifo /root/qin

echo "[$(date +%T)] launching QEMU..."
( tail -f /root/qin | timeout 240 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,hostfwd=tcp::2222-:22 -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p7lock.log 2>&1 ) &

echo "[$(date +%T)] waiting for shell..."
for i in $(seq 1 90); do
  if strings /root/p7lock.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "[$(date +%T)] prompt at ${i}s"; break; fi
  sleep 1
done
sleep 2

send() {
  timeout 5 bash -c "printf '%s\n' '$1' > /root/qin" 2>/dev/null || echo "[$(date +%T)] !! guest input fifo blocked"
  sleep "$2"
}

echo "[$(date +%T)] starting dhcp + sshd..."
send "dhcp" 12
send "sshd" 4

echo "[$(date +%T)] [0/4] utility --version smoke..."
send "ls --version" 3
send "wc --version" 3
ALOG0=$(strings /root/p7lock.log)
echo "$ALOG0" | grep -q 'NeutrinoOS 1.0.0' && ok "utility --version prints NeutrinoOS 1.0.0" || bad "utility --version prints NeutrinoOS 1.0.0"

echo "[$(date +%T)] waiting for ssh port..."
for i in $(seq 1 40); do
  if (exec 3<>/dev/tcp/127.0.0.1/2222) 2>/dev/null; then exec 3>&- 2>/dev/null; break; fi
  sleep 1
done

SSH="ssh -i /root/p6key -p 2222 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -o ConnectTimeout=8 user@127.0.0.1"

echo "[$(date +%T)] [1/4] key auth sanity..."
if $SSH true > /dev/null 2>&1; then ok "key auth before lockout"; else bad "key auth before lockout"; fi

echo "[$(date +%T)] [2/4] three wrong-password attempts..."
for k in 1 2 3; do
  SSH_PW_DEADLINE=12 python3 /mnt/d/Projects/Code/NeutrinoOS/build/p6-ssh-pw.py wrongpw-$k 127.0.0.1 2222 > /root/p7lock-pw-$k.log 2>&1 || true
  R=$(grep -ao 'Permission denied' /root/p7lock-pw-$k.log | head -1)
  [ -z "$R" ] && R=$(grep -ao 'PW-OK=True' /root/p7lock-pw-$k.log | head -1)
  echo "  attempt $k: ${R:-no-notice}"
done

echo "[$(date +%T)] [3/4] key auth must now be refused (banned)..."
if $SSH true > /dev/null 2>&1; then
  bad "connection from banned IP was accepted"
else
  ok "banned IP refused (key auth)"
fi

echo "[$(date +%T)] reading /var/log/auth.log over the console..."
send "cat /var/log/auth.log" 3
sleep 1
ALOG=$(strings /root/p7lock.log)
echo "$ALOG" | grep -q 'auth fail from ' && ok "auth.log records failures" || bad "auth.log records failures"
echo "$ALOG" | grep -q 'banning ' && ok "auth.log records ban" || bad "auth.log records ban"
echo "$ALOG" | grep -q 'refused banned ' && ok "auth.log records banned reject" || bad "auth.log records banned reject"

echo "[$(date +%T)] [4/4] waiting out the 10s ban (guest clock under TCG runs slow)..."
OK4=0
for k in 1 2 3; do
  sleep 12
  if $SSH true > /dev/null 2>&1; then OK4=1; break; fi
done
[ $OK4 -eq 1 ] && ok "key auth works after ban expires" || bad "key auth works after ban expires"

echo "=== summary: $([ $FAIL -eq 0 ] && echo ALL-PASS || echo HAS-FAILURES) ($(date +%T))"
exit $FAIL
