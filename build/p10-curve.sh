#!/bin/bash
# Throughput curve: 256/512/768/1024/1536/3072 MiB files on fresh volumes.
set -u
HOSTDIR=/mnt/d/Projects/Code/NeutrinoOS/tests/exfat-host
H=$HOSTDIR/bin/Debug/net10.0/exfat-host
cd "$HOSTDIR" && dotnet build -v q --nologo 2>&1 | grep -E 'error' | head -3

for MB in 256 512 768 1024 1536 3072; do
  IMG=/root/p10curve-$MB.img
  VOL=$(( MB * 2 + 64 ))
  rm -f "$IMG"
  "$H" "$IMG" format "$VOL" CV$MB >/dev/null 2>&1
  echo "--- ${MB} MiB file on ${VOL} MiB volume:"
  "$H" "$IMG" big /f.bin "$MB" 2>&1 | grep -E "big write|big read|MB/s"
done
