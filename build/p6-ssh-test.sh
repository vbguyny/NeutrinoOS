#!/bin/bash
# Phase 6: full SSH end-to-end test (QEMU user-net, hostfwd 2222->22).
#
# 1. (Host) ensure a test keypair exists; install its public half as
#    /home/user/.ssh/authorized_keys in the image.
# 2. Boot the guest, run dhcp + sshd.
# 3. From the host: publickey auth exec ("echo hello-from-ssh"),
#    a piped interactive shell session, and a negative auth check.
set -u
cd /root/neutrino
exec < /dev/null
export MTOOLS_SKIP_CHECK=1
echo "[$(date +%T)] p6-ssh-test start"
pkill -9 qemu-system 2>/dev/null || true
sleep 1

cleanup() {
  pkill -9 -f qemu-system 2>/dev/null || true
  rm -f /root/qin
}
trap cleanup EXIT

# ---- host keypair ----
if [ ! -f /root/p6key ]; then
  ssh-keygen -t ed25519 -N "" -f /root/p6key -q
fi
cp /root/p6key.pub /mnt/d/Projects/Code/NeutrinoOS/build/authorized_keys.pub

echo "[$(date +%T)] deploying image..."
bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-deploy.sh > /root/p6deploy.log 2>&1
echo "[$(date +%T)] installing users/config..."
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-image-extras.sh >> /root/p6deploy.log 2>&1
sleep 1
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin /root/p6ssh.log
mkfifo /root/qin

echo "[$(date +%T)] launching QEMU..."
( tail -f /root/qin | timeout 300 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,hostfwd=tcp::2222-:22 -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p6ssh.log 2>&1 ) &

echo "[$(date +%T)] waiting for shell..."
for i in $(seq 1 90); do
  if strings /root/p6ssh.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "[$(date +%T)] prompt at ${i}s"; break; fi
  if [ $((i % 15)) -eq 0 ]; then echo "[$(date +%T)]   ...still booting (${i}s)"; fi
  sleep 1
done
pgrep -f 'qemu-system-x86_64' > /dev/null || echo "[$(date +%T)] !! qemu is not running (boot failed?)"
sleep 2

send() {
  timeout 5 bash -c "printf '%s\n' '$1' > /root/qin" 2>/dev/null || echo "[$(date +%T)] !! guest input fifo blocked"
  sleep "$2"
}
echo "[$(date +%T)] [1/6] configuring network (dhcp)..."
send "dhcp" 6
echo "[$(date +%T)] [2/6] starting sshd..."
send "sshd" 3

SSHOPTS="-i /root/p6key -p 2222 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -o ConnectTimeout=15 -o ConnectionAttempts=1 -o LogLevel=ERROR"

echo "[3/6] exec test (echo)..."
OUT1=$(timeout 40 ssh $SSHOPTS user@127.0.0.1 "echo hello-from-ssh" 2>&1)
RC1=$?
echo "$OUT1" | tr -d '\r'
echo "rc=$RC1"

echo "[4/6] exec test (uname)..."
OUT2=$(timeout 40 ssh $SSHOPTS user@127.0.0.1 "uname" 2>&1)
RC2=$?
echo "$OUT2" | tr -d '\r'
echo "rc=$RC2"

echo "[5/6] piped shell session..."
OUT3=$(printf 'uptime\nhelp\nexit\n' | timeout 60 ssh -tt $SSHOPTS user@127.0.0.1 2>&1 | tr -d '\r')
RC3=$?
echo "$OUT3" | tail -20
echo "rc=$RC3"

echo "[6/6] negative auth (wrong key)..."
rm -f /root/p6badkey /root/p6badkey.pub
ssh-keygen -t ed25519 -N "" -f /root/p6badkey -q 2>/dev/null || true
OUT4=$(timeout 30 ssh -i /root/p6badkey -p 2222 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -o ConnectTimeout=10 -o LogLevel=ERROR user@127.0.0.1 "echo nope" 2>&1)
RC4=$?
echo "$OUT4"
echo "rc=$RC4 (expected != 0)"

echo "[7/7] password auth (pty driver)..."
OUT5=$(timeout 60 python3 /mnt/d/Projects/Code/NeutrinoOS/build/p6-ssh-pw.py neutrino 2>&1)
RC5=$?
echo "$OUT5" | tail -6
echo "rc=$RC5 (expected 0)"

echo "=== guest sshd log:"
strings /root/p6ssh.log | grep -a '\[sshd\]' | tail -10
echo "=== SYSTEM HALTED count:"
strings /root/p6ssh.log | grep -sc 'SYSTEM HALTED' || true
