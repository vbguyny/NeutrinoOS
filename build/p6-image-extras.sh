#!/bin/bash
# Phase 6: add user accounts, ssh config and an (optional) authorized key
# to a FAT image (default /root/run.img; pass a path to target another image).
set -u
IMG="${1:-/root/run.img}"

# Never wait for input (mtools prompts would deadlock under wsl.exe).
exec < /dev/null
export MTOOLS_SKIP_CHECK=1

# Python-generate a scrypt password hash in the NeutrinoOS shadow format.
gen_hash() {
  python3 - "$1" <<'EOF'
import hashlib, os, sys
pw = sys.argv[1].encode()
salt = os.urandom(16)
dk = hashlib.scrypt(pw, salt=salt, n=16384, r=8, p=1, dklen=32)
print("scrypt$16384$8$1$%s$%s" % (salt.hex(), dk.hex()))
EOF
}

ROOT_HASH=$(gen_hash "neutrino")
USER_HASH=$(gen_hash "neutrino")

cat > /root/p6-passwd <<EOF
root:x:0:0:root:/root:/bin/shell.dll
user:x:1000:1000:Neutrino User:/home/user:/bin/shell.dll
EOF

cat > /root/p6-shadow <<EOF
root:${ROOT_HASH}:
user:${USER_HASH}:
EOF

cat > /root/p6-sshd-config <<'EOF'
# NeutrinoOS sshd configuration (key=value)
Port=22
# Phase 7: password auth is OFF by default; this test fixture enables it
# explicitly so the acceptance scripts can exercise password login.
PasswordAuthentication=yes
# Phase 7 test fixture: fast lockout so the acceptance probe finishes quickly.
BanThreshold=3
BanSeconds=10
MaxAuthAttempts=3
EOF

# Create a directory only when missing. mmd on an EXISTING directory
# prompts for confirmation on /dev/tty, which deadlocks in script
# chains (stdin redirection does not help - mtools bypasses it).
mk_dir() {
  if ! timeout -s KILL 10 mdir -i "$IMG" "::$1" > /dev/null 2>&1; then
    timeout -s KILL 10 mmd -i "$IMG" "::$1" > /dev/null 2>&1 || true
  fi
}

mk_dir /etc
mk_dir /etc/ssh
mk_dir /root
mk_dir /root/.ssh
mk_dir /home
mk_dir /home/user
mk_dir /home/user/.ssh
mk_dir /var
mk_dir /var/log

timeout -s KILL 20 mcopy -i "$IMG" -o /root/p6-passwd ::/etc/passwd
timeout -s KILL 20 mcopy -i "$IMG" -o /root/p6-shadow ::/etc/shadow
timeout -s KILL 20 mcopy -i "$IMG" -o /root/p6-sshd-config ::/etc/ssh/sshd_config

# Optional: authorized_keys for pubkey auth (from build/authorized_keys.pub).
AK=/mnt/d/Projects/Code/NeutrinoOS/build/authorized_keys.pub
if [ -f "$AK" ]; then
  timeout -s KILL 20 mcopy -i "$IMG" -o "$AK" ::/home/user/.ssh/authorized_keys
  echo "== authorized_keys installed (pubkey auth enabled for user)"
else
  echo "== no build/authorized_keys.pub - password auth only"
fi

echo "== phase6 image extras done"
mdir -i "$IMG" ::/etc
