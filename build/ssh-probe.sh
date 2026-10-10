#!/bin/bash
# SSH end-to-end verification probe (docs/SSH.md):
# boots the CLI image with the user fixture + a fresh test key, starts
# sshd, then exercises the full feature set from the WSL host through
# slirp hostfwd (2222 -> 22): publickey exec, interactive shell with
# cursor editing + history, password auth, the SFTP subsystem
# (sftp batch + modern scp + 2 MiB chunked transfers), client-initiated
# rekey, and negative auth. Prints PASS/FAIL per check + summary.
set -u
cd /root/neutrino
echo "image: $(md5sum build/x64/neutrinoos-cli.img | cut -c1-8)"
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
sleep 1

# ---- test keypair + image fixture ----
if [ ! -f /root/p6key ]; then
  ssh-keygen -t ed25519 -N "" -f /root/p6key -q
fi
cp -f build/x64/neutrinoos-cli.img /tmp/sshprobe.img
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-image-extras.sh /tmp/sshprobe.img > /tmp/sshprobe-extras.log 2>&1
export MTOOLS_SKIP_CHECK=1
mcopy -o -i /tmp/sshprobe.img /root/p6key.pub ::/home/user/.ssh/authorized_keys
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-sshprobe.fd
rm -f /root/qin-ssh /root/sshprobe.out
touch /root/qin-ssh
setsid bash -c "tail -f /root/qin-ssh | qemu-system-x86_64 -machine q35 -m 2G -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-sshprobe.fd \
  -drive format=raw,file=/tmp/sshprobe.img \
  -netdev user,id=n0,hostfwd=tcp::2222-:22 \
  -device virtio-net-pci,netdev=n0 \
  -vga std -display none -serial stdio -no-reboot -no-shutdown" < /dev/null > /root/sshprobe.out 2>&1 &

for i in $(seq 1 120); do
  grep -aq 'Type .help.' /root/sshprobe.out 2>/dev/null && break
  sleep 0.5
done
echo BOOTED
printf 'sshd\n' >> /root/qin-ssh
sleep 4

PASSED=0; FAILED=0
pass() { echo "PASS: $1"; PASSED=$((PASSED + 1)); }
fail() { echo "FAIL: $1"; FAILED=$((FAILED + 1)); }

SSHOPTS="-i /root/p6key -p 2222 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -o ConnectTimeout=15 -o ConnectionAttempts=1 -o LogLevel=ERROR"

# Wait until the listener answers (eth0 autoconfig can lag the prompt).
for i in $(seq 1 20); do
  timeout 20 ssh $SSHOPTS user@127.0.0.1 "echo READY" 2>/dev/null | grep -q READY && break
  sleep 1
done

echo "=== exec (publickey) ==="
OUT=$(timeout 45 ssh $SSHOPTS user@127.0.0.1 "uname" 2>&1 | tr -d '\r')
[ "$OUT" = "NeutrinoOS" ] && pass "exec uname (pubkey)" || fail "exec uname ($OUT)"
OUT=$(timeout 45 ssh $SSHOPTS user@127.0.0.1 "echo hello-from-ssh" 2>&1 | tr -d '\r')
[ "$OUT" = "hello-from-ssh" ] && pass "exec echo" || fail "exec echo ($OUT)"
OUT=$(timeout 45 ssh $SSHOPTS user@127.0.0.1 "free" 2>&1 | tr -d '\r')
case "$OUT" in *Mem*|*mem*|*bytes*|*Bytes*) pass "exec captures utility output";; *) fail "exec utility ($OUT)";; esac

echo "=== interactive shell (-tt) ==="
OUT=$(printf 'uptime\nhelp\nexit\n' | timeout 90 ssh -tt $SSHOPTS user@127.0.0.1 2>&1 | tr -d '\r')
RC=$?
case "$OUT" in *"Welcome to NeutrinoOS"*) pass "welcome banner";; *) fail "welcome banner";; esac
case "$OUT" in *"logout"*) pass "clean logout (exit)";; *) fail "logout";; esac
[ $RC -eq 0 ] && pass "interactive rc=0" || fail "interactive rc=$RC"

echo "=== line editing + history ==="
OUT=$(printf 'echo axc\x1b[D\x1b[Db\r' | timeout 90 ssh -tt $SSHOPTS user@127.0.0.1 2>&1 | tr -d '\r')
case "$OUT" in *abxc*) pass "cursor-left + mid-line insert";; *) fail "cursor-left insert ($OUT)";; esac
OUT=$(printf 'echo homeline\x0d\x1b[A\x0d' | timeout 90 ssh -tt $SSHOPTS user@127.0.0.1 2>&1 | tr -d '\r')
COUNT=$(printf '%s' "$OUT" | grep -o 'homeline' | wc -l)
[ "$COUNT" -ge 3 ] && pass "history recall (up-arrow)" || fail "history recall (count=$COUNT)"

echo "=== rekey (client-initiated) ==="
OUT=$(printf 'seq 1 300\nexit\n' | timeout 120 ssh -tt -o RekeyLimit=1K $SSHOPTS user@127.0.0.1 2>&1 | tr -d '\r')
case "$OUT" in *"300"*) pass "session survives forced rekey (seq 1 300)";; *) fail "rekey session";; esac
OUT=$(timeout 45 ssh -o RekeyLimit=1K $SSHOPTS user@127.0.0.1 "echo rekey-exec" 2>&1 | tr -d '\r')
[ "$OUT" = "rekey-exec" ] && pass "exec after rekey" || fail "exec after rekey ($OUT)"

echo "=== password auth ==="
OUT=$(timeout 90 python3 /mnt/d/Projects/Code/NeutrinoOS/build/p6-ssh-pw.py neutrino 2>&1 | tail -2)
case "$OUT" in *PW-OK*) pass "password login";; *) fail "password login ($OUT)";; esac

echo "=== SFTP subsystem ==="
printf 'sftp-upload-content-123\n' > /tmp/ssh-upload.txt
timeout 60 sftp -q -b - -P 2222 -i /root/p6key -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR user@127.0.0.1 > /tmp/ssh-sftp.log 2>&1 <<'SFTPEOF'
put /tmp/ssh-upload.txt /home/user/uploaded.txt
ls -l /home/user
get /home/user/uploaded.txt /tmp/ssh-download.txt
rename /home/user/uploaded.txt /home/user/renamed.txt
rm /home/user/renamed.txt
mkdir /home/user/dirx
rmdir /home/user/dirx
bye
SFTPEOF
SFTPRC=$?
[ $SFTPRC -eq 0 ] && pass "sftp batch (put/ls/get/rename/rm/mkdir/rmdir)" || fail "sftp batch rc=$SFTPRC: $(tail -3 /tmp/ssh-sftp.log)"
if cmp -s /tmp/ssh-upload.txt /tmp/ssh-download.txt; then
  pass "sftp round-trip content identical"
else
  fail "sftp round-trip content differs"
fi

echo "=== scp (modern client uses the SFTP subsystem) ==="
timeout 60 scp -q -P 2222 -i /root/p6key -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR /tmp/ssh-upload.txt user@127.0.0.1:/home/user/scp-up.txt 2>/tmp/ssh-scp1.log
[ $? -eq 0 ] && pass "scp upload" || fail "scp upload: $(tail -2 /tmp/ssh-scp1.log)"
rm -f /tmp/ssh-scp-down.txt
timeout 60 scp -q -P 2222 -i /root/p6key -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR user@127.0.0.1:/home/user/scp-up.txt /tmp/ssh-scp-down.txt 2>/tmp/ssh-scp2.log
[ $? -eq 0 ] && pass "scp download" || fail "scp download: $(tail -2 /tmp/ssh-scp2.log)"
cmp -s /tmp/ssh-upload.txt /tmp/ssh-scp-down.txt && pass "scp round-trip content identical" || fail "scp round-trip differs"

echo "=== large file transfer (chunked VFS, beyond the old 64 KiB cap) ==="
dd if=/dev/urandom of=/tmp/ssh-big.bin bs=1024 count=2048 2>/dev/null
BIGSZ=$(stat -c %s /tmp/ssh-big.bin)
timeout 240 scp -q -P 2222 -i /root/p6key -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR /tmp/ssh-big.bin user@127.0.0.1:/home/user/big-up.bin 2>/tmp/ssh-big1.log
[ $? -eq 0 ] && pass "scp 2 MiB upload (chunked writes)" || fail "scp 2 MiB upload: $(tail -2 /tmp/ssh-big1.log)"
rm -f /tmp/ssh-big-down.bin
timeout 240 scp -q -P 2222 -i /root/p6key -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR user@127.0.0.1:/home/user/big-up.bin /tmp/ssh-big-down.bin 2>/tmp/ssh-big2.log
[ $? -eq 0 ] && pass "scp 2 MiB download (chunked reads)" || fail "scp 2 MiB download: $(tail -2 /tmp/ssh-big2.log)"
DOWNSZ=$(stat -c %s /tmp/ssh-big-down.bin 2>/dev/null || echo 0)
if cmp -s /tmp/ssh-big.bin /tmp/ssh-big-down.bin; then
  pass "2 MiB round-trip byte-identical ($BIGSZ bytes)"
else
  fail "2 MiB round-trip differs (down=$DOWNSZ up=$BIGSZ)"
fi

echo "=== negative auth (kept last: lockout fixture bans after 3) ==="
if [ ! -f /root/sshbadkey ]; then ssh-keygen -t ed25519 -N "" -f /root/sshbadkey -q; fi
timeout 30 ssh -i /root/sshbadkey -p 2222 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -o ConnectTimeout=10 -o LogLevel=ERROR user@127.0.0.1 "echo nope" > /dev/null 2>&1
RC=$?
[ $RC -ne 0 ] && pass "wrong key refused (rc=$RC)" || fail "wrong key accepted"
OUT=$(timeout 60 python3 /mnt/d/Projects/Code/NeutrinoOS/build/p6-ssh-pw.py wrongpassword 2>&1 | tail -1)
case "$OUT" in *"PW-OK=False"*) pass "wrong password refused";; *) fail "wrong password ($OUT)";; esac

echo
echo "===================="
echo "PASSED=$PASSED FAILED=$FAILED"
echo "===================="
strings /root/sshprobe.out | grep -a '\[sshd\]' | tail -8
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
