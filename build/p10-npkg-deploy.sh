#!/bin/bash
# Phase 10 npkg acceptance image: neutrinoos.img + /bin utilities WITHOUT
# the exFAT tools + /repo (signed neutrinoos.utils.exfat package) + the
# repo trust anchor + a workspace. The p10-npkg-test.sh then installs the
# package on-device and runs the installed tools.
#
#   bash build/p10-package.sh            (build the repo first)
#   bash build/p10-npkg-deploy.sh
set -euo pipefail
export MTOOLS_SKIP_CHECK=1
exec < /dev/null

cd /root/neutrino

IMG=/root/p10npkg.img
REPO=/root/p10npkg/repo
UTILBIN=/root/phase5bin
TMP=/root/p10pkgtmp

test -f build/x64/neutrinoos.img || { echo "missing build/x64/neutrinoos.img"; exit 1; }
test -d "$UTILBIN" || { echo "missing $UTILBIN - run p5-apps-build.sh"; exit 1; }
test -f "$REPO/repository.json" || { echo "missing $REPO - run build/p10-package.sh"; exit 1; }

rm -rf "$TMP"; mkdir -p "$TMP"
cp build/x64/neutrinoos.img "$IMG"

mk_dir() { timeout -s KILL 20 mdir -i "$IMG" "::/$1" >/dev/null 2>&1 || timeout -s KILL 20 mmd -i "$IMG" "::/$1"; }

mk_dir bin
mk_dir etc
mk_dir etc/npkg
mk_dir etc/npkg/trusted-keys
mk_dir repo
mk_dir var
mk_dir var/lib

# Utilities, minus the four exFAT tools (npkg installs those).
for f in "$UTILBIN"/*.dll; do
  base=$(basename "$f")
  case "$base" in
    mkexfat.dll|fsck.exfat.dll|exfatlabel.dll|exfatattrib.dll) continue ;;
  esac
  timeout -s KILL 60 mcopy -i "$IMG" -o "$f" "::/bin/$base"
done

printf 'export PATH=/bin:/apps\n' > "$TMP/profile"
printf 'export TERM=vt100\n' >> "$TMP/profile"
timeout -s KILL 20 mcopy -i "$IMG" -o "$TMP/profile" "::/etc/profile"

# Repository + trust anchor for the exFAT package.
for f in "$REPO"/*.npkg "$REPO"/repository.json "$REPO"/repository.json.sig "$REPO"/repo.pub; do
  [ -f "$f" ] || continue
  timeout -s KILL 60 mcopy -i "$IMG" -o "$f" "::/repo/$(basename "$f")"
done
timeout -s KILL 20 mcopy -i "$IMG" -o "$REPO/repo.pub" "::/etc/npkg/trusted-keys/neutrinoos-utils.pub"

echo 1 > "$TMP/skip-boot-tests"
timeout -s KILL 20 mcopy -i "$IMG" -o "$TMP/skip-boot-tests" "::/skip-boot-tests" 2>/dev/null || true

echo "=== deployed image $IMG ==="
mdir -i "$IMG" ::/bin | head -8
mdir -i "$IMG" ::/repo
echo "deploy OK"
