#!/bin/bash
# Sync sources from the Windows repo, rebuild the image (with korlib artifact
# cleanup), and report. The Windows repository is a fresh git repo (no shared
# history with the WSL clone), so sync via rsync overlay instead of git pull.
set -euo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Optional: TRACE=1 in the environment (or --trace as the first argument)
# compiles the runtime trace prints in (src/kernel/Runtime/JitTrace.cs).
if [ "${1:-}" = "--trace" ]; then
  export TRACE=1
fi

SRC=/mnt/d/Projects/Code/NeutrinoOS

# Stamp this build in the SOURCE repo before syncing: .buildnum increments
# and src/kernel/Generated/NeutrinoVersion.cs is regenerated there, then the
# rsync below carries the generated file into the WSL build tree.
bash "$SRC/version-bump.sh"

cd /root/neutrino
if command -v rsync >/dev/null 2>&1; then
  rsync -a --delete --exclude 'obj' --exclude 'bin' "$SRC/src/" /root/neutrino/src/
  rsync -a "$SRC/tests/" /root/neutrino/tests/
else
  cp -a "$SRC/src/." /root/neutrino/src/
  cp -a "$SRC/tests/." /root/neutrino/tests/
fi
cp -f "$SRC/Makefile" /root/neutrino/Makefile
# Keep the WSL copy's build entry points current (a future ./build.sh in
# /root/neutrino must bump the same counter sequence).
cp -f "$SRC/build.sh" "$SRC/version-bump.sh" "$SRC/.buildnum" /root/neutrino/ 2>/dev/null || true
echo "Synced from $SRC"

rm -rf src/korlib/obj src/korlib/bin
rm -f build/x64/kernel.obj build/x64/BOOTX64.EFI
make image
echo "=== REBUILD OK ==="
ls -la build/x64/neutrinoos.img build/x64/BOOTX64.EFI
