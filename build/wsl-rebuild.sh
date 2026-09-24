#!/bin/bash
# Sync sources from the Windows repo, rebuild the image (with korlib artifact
# cleanup), and report. The Windows repository is a fresh git repo (no shared
# history with the WSL clone), so sync via rsync overlay instead of git pull.
set -euo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
cd /root/neutrino
if command -v rsync >/dev/null 2>&1; then
  rsync -a --delete --exclude 'obj' --exclude 'bin' "$SRC/src/" /root/neutrino/src/
  rsync -a "$SRC/tests/" /root/neutrino/tests/
else
  cp -a "$SRC/src/." /root/neutrino/src/
  cp -a "$SRC/tests/." /root/neutrino/tests/
fi
cp -f "$SRC/Makefile" /root/neutrino/Makefile
echo "Synced from $SRC"

rm -rf src/korlib/obj src/korlib/bin
rm -f build/x64/kernel.obj build/x64/BOOTX64.EFI
make image
echo "=== REBUILD OK ==="
ls -la build/x64/neutrinoos.img build/x64/BOOTX64.EFI
