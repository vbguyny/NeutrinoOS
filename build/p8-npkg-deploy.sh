#!/bin/bash
# Phase 8: build the npkg device-test image.
#   base image + /bin utilities + /etc/profile + /repo (signed test repo)
#   + /etc/npkg/trusted-keys/test.pub + skip-boot-tests marker.
# Output: /root/npkgtest.img (also copied to build/npkgtest.img on Windows).
set -euo pipefail
export MTOOLS_SKIP_CHECK=1
exec < /dev/null

cd /root/neutrino

SRC=/mnt/d/Projects/Code/NeutrinoOS
UTILBIN=/root/phase5bin
REPO=/root/p8repo
IMG=/root/npkgtest.img
TMP=/root/p8deploy

rm -rf "$TMP"; mkdir -p "$TMP"

test -f build/x64/neutrinoos.img || { echo "missing build/x64/neutrinoos.img - run wsl-rebuild first"; exit 1; }
test -d "$UTILBIN" || { echo "missing $UTILBIN - run p5-apps-build.sh first"; exit 1; }
test -f "$REPO/repository.json" || { echo "missing $REPO/repository.json - run p8-npkg-tests-build.sh first"; exit 1; }

cp build/x64/neutrinoos.img "$IMG"

mk_dir() { timeout -s KILL 20 mdir -i "$IMG" "::/$1" >/dev/null 2>&1 || timeout -s KILL 20 mmd -i "$IMG" "::/$1"; }

mk_dir bin
mk_dir etc
mk_dir etc/npkg
mk_dir etc/npkg/trusted-keys
mk_dir repo

# Utilities (includes npkg.dll).
timeout -s KILL 60 mcopy -i "$IMG" "$UTILBIN"/*.dll "::/bin/"

# Shell profile with $PATH (same as the Phase 5 deploy).
printf 'export PATH=/bin:/apps\n' > "$TMP/profile"
timeout -s KILL 20 mcopy -i "$IMG" "$TMP/profile" "::/etc/profile"

# Test repository (packages + signed index + published public key).
timeout -s KILL 60 mcopy -i "$IMG" "$REPO"/*.npkg "$REPO/repository.json" "$REPO/repository.json.sig" "$REPO/repo.pub" "::/repo/"

# Trust the test signing key (the "local" repo is added with this too).
timeout -s KILL 20 mcopy -i "$IMG" "$SRC/tests/npkg/keys/public.key" "::/etc/npkg/trusted-keys/test.pub"

# Skip the JIT boot-test suites: keep the npkg test boots fast.
printf '1' > "$TMP/skip-boot-tests"
timeout -s KILL 20 mcopy -i "$IMG" "$TMP/skip-boot-tests" "::/skip-boot-tests"

# TEMP: verbose JIT logging ([JIT] Compile #N asm= tok=0x...) for crash triage.
# Re-enabled for the [PHYS] eval-stack desync diagnostic run.
# DISABLED again: callvirt large-struct arg fix applied; keep boots fast.
# printf '1' > "$TMP/verbose-jit"
# timeout -s KILL 20 mcopy -i "$IMG" "$TMP/verbose-jit" "::/verbose-jit"

cp "$IMG" "$SRC/build/npkgtest.img"

echo "=== NPKG IMAGE OK ==="
timeout -s KILL 20 mdir -i "$IMG" "::/" | head -14
echo "--- /repo:"
timeout -s KILL 20 mdir -i "$IMG" "::/repo" | tail -6
