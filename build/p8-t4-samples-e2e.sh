#!/bin/bash
# Task 4 samples e2e: build all five samples, assert auto-packaged .npkg
# files, the project-reference payload inclusion, and the driver
# manifest-pack flow.
set -uo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
PASS=0
FAIL=0
check() { if [ "$2" -eq 0 ]; then echo "PASS: $1"; PASS=$((PASS+1)); else echo "FAIL: $1"; FAIL=$((FAIL+1)); fi; }

export PATH="/root/p8sdk:$PATH"
command -v npkg-host >/dev/null || bash "$SRC/build/p8-sdk-build.sh" > /tmp/t4-samp-prereq.log 2>&1
command -v npkg-host >/dev/null
check "npkg-host on PATH" $?

echo "--- build samples (Release) ---"
for s in hello-library hello-console file-utility hello-driver hello-webapp; do
  ( cd "$SRC/samples/$s" && dotnet build -c Release > "/tmp/t4-samp-$s.log" 2>&1 )
  check "build samples/$s" $?
done

LIB_PKG="$SRC/samples/hello-library/bin/Release/net10.0/hellolib.npkg"
CON_PKG="$SRC/samples/hello-console/bin/Release/net10.0/hellocon.npkg"
TOOL_PKG="$SRC/samples/file-utility/bin/Release/net10.0/filetool.npkg"
WEB_PKG="$SRC/samples/hello-webapp/bin/Release/net10.0/hello-webapp.npkg"
DRV_DLL="$SRC/samples/hello-driver/bin/Release/net10.0/leddrv.dll"

[ -f "$LIB_PKG" ] && check "hellolib.npkg auto-packaged" 0 || check "hellolib.npkg auto-packaged" 1
[ -f "$CON_PKG" ] && check "hellocon.npkg auto-packaged" 0 || check "hellocon.npkg auto-packaged" 1
[ -f "$TOOL_PKG" ] && check "filetool.npkg auto-packaged" 0 || check "filetool.npkg auto-packaged" 1
[ -f "$WEB_PKG" ] && check "hello-webapp.npkg auto-packaged" 0 || check "hello-webapp.npkg auto-packaged" 1
[ -f "$DRV_DLL" ] && check "leddrv.dll built" 0 || check "leddrv.dll built" 1

echo "--- package contents ---"
npkg-host list "$CON_PKG" > /tmp/t4-con-list.log 2>&1
grep -q 'hellocon.dll' /tmp/t4-con-list.log && grep -q 'hellolib.dll' /tmp/t4-con-list.log
check "hellocon payload includes referenced hellolib.dll" $?
grep -q 'install path: lib' /tmp/t4-lib-list.log 2>/dev/null || npkg-host list "$LIB_PKG" > /tmp/t4-lib-list.log 2>&1
grep -q 'install path: lib' /tmp/t4-lib-list.log
check "hellolib installs to /lib" $?
npkg-host list "$TOOL_PKG" > /tmp/t4-tool-list.log 2>&1
grep -q 'install path: bin' /tmp/t4-tool-list.log
check "filetool installs to /bin" $?

npkg-host verify "$CON_PKG" > /dev/null 2>&1
check "hellocon.npkg verifies" $?
npkg-host verify "$WEB_PKG" > /dev/null 2>&1
check "hello-webapp.npkg verifies" $?

echo "--- driver manifest-pack flow ---"
npkg-host pack --manifest "$SRC/samples/hello-driver/manifest.json" \
               --payload-dir "$SRC/samples/hello-driver/bin/Release/net10.0" \
               --out /tmp/hello-led.npkg > /tmp/t4-drvpack.log 2>&1
check "driver packed from manifest.json" $?
npkg-host verify /tmp/hello-led.npkg > /dev/null 2>&1
check "driver package verifies" $?
npkg-host list /tmp/hello-led.npkg | grep -q 'leddrv.dll'
check "driver payload is leddrv.dll" $?

if [ "$FAIL" -eq 0 ]; then
  echo "=== t4 samples summary: ALL-PASS (${PASS} checks) ==="
else
  echo "=== t4 samples summary: FAIL (${PASS} passed, ${FAIL} failed) ==="
  for f in /tmp/t4-samp-*.log; do echo "--- $f"; tail -12 "$f"; done
fi
