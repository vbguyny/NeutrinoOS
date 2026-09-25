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

# --- Pre-placed driver package -------------------------------------------
# The kernel driver-package loader reads /var/lib/npkg/installed.json and
# loads packages under /var/lib/npkg/drivers/<name>/. Pre-place the
# hello-driver payload under a distinct name so the loader is exercised on
# the very first boot (the acceptance suite later installs tests.hello-driver
# itself, unchanged, and final-list tolerates extra packages).
mk_dir var
mk_dir var/lib
mk_dir var/lib/npkg
mk_dir var/lib/npkg/drivers
mk_dir var/lib/npkg/drivers/preplaced.drvtest

PLACE="$TMP/preplaced"
rm -rf "$PLACE"
mkdir -p "$PLACE"
python3 - "$REPO/tests.hello-driver-1.0.0.npkg" "$PLACE" <<'PY'
import os, sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
out = sys.argv[2]
z.extract("manifest.json", out)
z.extract("payload/hellodrv.dll", out)
os.replace(os.path.join(out, "payload", "hellodrv.dll"),
           os.path.join(out, "hellodrv.dll"))
os.rmdir(os.path.join(out, "payload"))
PY
timeout -s KILL 20 mcopy -i "$IMG" "$PLACE/hellodrv.dll" "$PLACE/manifest.json" "::/var/lib/npkg/drivers/preplaced.drvtest/"

cat > "$TMP/installed.json" <<'EOF'
{"format":"npkg-db/1","packages":[{"name":"preplaced.drvtest","version":"1.0.0","architecture":"any","signer":"65b60673","description":"pre-placed driver package (loader acceptance)","installPath":"/var/lib/npkg/drivers/preplaced.drvtest","provides":["driver"],"dependencies":{},"scripts":{},"entryPoints":{},"files":["/var/lib/npkg/drivers/preplaced.drvtest/hellodrv.dll","/var/lib/npkg/drivers/preplaced.drvtest/manifest.json"],"dirs":["/var/lib/npkg/drivers/preplaced.drvtest"]}]}
EOF
timeout -s KILL 20 mcopy -i "$IMG" "$TMP/installed.json" "::/var/lib/npkg/installed.json"

cp "$IMG" "$SRC/build/npkgtest.img"

echo "=== NPKG IMAGE OK ==="
timeout -s KILL 20 mdir -i "$IMG" "::/" | head -14
echo "--- /repo:"
timeout -s KILL 20 mdir -i "$IMG" "::/repo" | tail -6
