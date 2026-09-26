#!/bin/bash
# Task 5: generate the official NeutrinoOS project signing key into keys/.
set -e
bash /mnt/d/Projects/Code/NeutrinoOS/build/p8-sdk-build.sh > /tmp/p8sdk-keygen.log 2>&1
mkdir -p /mnt/d/Projects/Code/NeutrinoOS/keys
/root/p8sdk/npkg-host keygen --out-dir /mnt/d/Projects/Code/NeutrinoOS/keys --force
echo "=== fingerprint ==="
/root/p8sdk/npkg-host fingerprint /mnt/d/Projects/Code/NeutrinoOS/keys/public.key
echo "=== files ==="
ls -la /mnt/d/Projects/Code/NeutrinoOS/keys
