#!/bin/bash
# Sync sources from the Windows repo into the WSL build tree and rebuild
# incrementally. The Windows repository is a fresh git repo (no shared history
# with the WSL clone), so sync via rsync overlay instead of git pull.
#
# Incremental: rsync preserves the mtime of unchanged files, so make only
# recompiles what actually changed and only reassembles the boot image when one
# of its contents is newer. Typical costs:
#   nothing changed     ~1 s   (rsync + make no-op)
#   one app/utility     ~5 s   (that project + image)
#   kernel sources     ~15 s   (one bflat run + image)
#
# Options:
#   --trace     compile in the runtime trace prints (src/kernel/Runtime/JitTrace.cs)
#   --clean     wipe build/x64 and korlib's obj/bin first (full rebuild)
#   --no-bump   do not increment .buildnum / regenerate NeutrinoVersion.cs
#               (keeps the kernel cacheable when iterating)
#   -j N        make parallelism (default: CPU count)
set -euo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

CLEAN=0
BUMP=1
JOBS=""
while [ $# -gt 0 ]; do
  case "$1" in
    --trace)   export TRACE=1 ;;
    --clean)   CLEAN=1 ;;
    --no-bump) BUMP=0 ;;
    -j|--jobs)
      [ $# -ge 2 ] || { echo "wsl-rebuild.sh: $1 needs a value" >&2; exit 2; }
      JOBS="$2"; shift ;;
    *) echo "wsl-rebuild.sh: unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done
if [ -z "$JOBS" ]; then
  JOBS="$( (command -v nproc >/dev/null 2>&1 && nproc) || echo 4)"
fi

SRC=/mnt/d/Projects/Code/NeutrinoOS

# Stamp this build in the SOURCE repo before syncing: .buildnum increments
# and src/kernel/Generated/NeutrinoVersion.cs is regenerated there, then the
# rsync below carries the generated file into the WSL build tree.
if [ "$BUMP" = "1" ]; then
  bash "$SRC/version-bump.sh"
fi

t0=$(date +%s)
cd /root/neutrino
# WSL reports /mnt/d mtimes verbatim and the Windows clock can run ahead of the
# WSL clock, so freshly saved sources often look "newer" than every artifact and
# defeat incremental builds. Stamp the time before syncing, then clamp anything
# newer than that stamp to now (only files that were just copied can be).
sync_stamp=$(mktemp)
touch "$sync_stamp"
trap 'rm -f "$sync_stamp"' EXIT
if command -v rsync >/dev/null 2>&1; then
  rsync -a -c --delete --exclude 'obj' --exclude 'bin' "$SRC/src/" /root/neutrino/src/
  rsync -a -c "$SRC/tests/" /root/neutrino/tests/
else
  cp -a "$SRC/src/." /root/neutrino/src/
  cp -a "$SRC/tests/." /root/neutrino/tests/
fi
find src tests -newer "$sync_stamp" \
     \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.txt' -o -name '*.asm' -o -name '*.s' \) \
     -exec touch {} + 2>/dev/null || true
cp -f "$SRC/Makefile" /root/neutrino/Makefile
# Keep the WSL copy's build entry points current (a future ./build.sh in
# /root/neutrino must bump the same counter sequence).
cp -f "$SRC/build.sh" "$SRC/version-bump.sh" "$SRC/.buildnum" /root/neutrino/ 2>/dev/null || true
echo "Synced from $SRC"

if [ "$CLEAN" = "1" ]; then
  rm -rf build/x64 src/korlib/obj src/korlib/bin
fi

make -j"$JOBS" image
t1=$(date +%s)
echo "=== REBUILD OK === ($((t1-t0)) s: sync + make -j$JOBS)"
ls -la build/x64/neutrinoos.img build/x64/BOOTX64.EFI
