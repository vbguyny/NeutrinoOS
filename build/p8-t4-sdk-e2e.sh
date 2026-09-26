#!/bin/bash
# Task 4 SDK end-to-end: build the host npkg tool, install the dotnet new
# templates, create one project per template, Release-build them and assert
# the auto-generated .npkg packages.
set -uo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
WORK=/root/t4sdk
PASS=0
FAIL=0
check() { if [ "$2" -eq 0 ]; then echo "PASS: $1"; PASS=$((PASS+1)); else echo "FAIL: $1"; FAIL=$((FAIL+1)); fi; }

echo "--- build host npkg tool ---"
bash "$SRC/build/p8-sdk-build.sh" > /tmp/t4-npkgbuild.log 2>&1
test -x /root/p8sdk/npkg-host
check "npkg-host built" $?
export PATH="/root/p8sdk:$PATH"

echo "--- install templates ---"
rm -rf "$WORK"; mkdir -p "$WORK"; cd "$WORK"
dotnet new install "$SRC/templates" --force > /tmp/t4-tplinstall.log 2>&1
check "dotnet new install (5 templates)" $?
dotnet new list 2>/dev/null | grep -q neutrino-console
check "template listed: neutrino-console" $?

echo "--- instantiate + build ---"
dotnet new neutrino-console -n MyApp -o "$WORK/MyApp" > /tmp/t4-new1.log 2>&1
check "dotnet new neutrino-console" $?
dotnet new neutrino-utility -n mytool -o "$WORK/mytool" > /tmp/t4-new2.log 2>&1
check "dotnet new neutrino-utility" $?
dotnet new neutrino-library -n mylib -o "$WORK/mylib" > /tmp/t4-new3.log 2>&1
check "dotnet new neutrino-library" $?
dotnet new neutrino-driver -n led -o "$WORK/led" > /tmp/t4-new4.log 2>&1
check "dotnet new neutrino-driver" $?
dotnet new neutrino-webapp -n webapp1 -o "$WORK/webapp1" > /tmp/t4-new5.log 2>&1
check "dotnet new neutrino-webapp" $?

echo "--- Release builds (auto-pack) ---"
for p in MyApp mytool mylib; do
  ( cd "$WORK/$p" && dotnet build -c Release > "/tmp/t4-build-$p.log" 2>&1 )
  check "build $p" $?
done

test -f "$WORK/MyApp/bin/Release/net10.0/MyApp.npkg"
check "MyApp.npkg auto-packaged (app)" $?
test -f "$WORK/mytool/bin/Release/net10.0/mytool.npkg"
check "mytool.npkg auto-packaged (utility)" $?
test -f "$WORK/mylib/bin/Release/net10.0/mylib.npkg"
check "mylib.npkg auto-packaged (library)" $?

echo "--- verify packages ---"
npkg-host verify "$WORK/MyApp/bin/Release/net10.0/MyApp.npkg" > /tmp/t4-verify.log 2>&1
check "npkg-host verify MyApp.npkg" $?
grep -a 'PASS' /tmp/t4-verify.log | head -5

if [ "$FAIL" -eq 0 ]; then
  echo "=== t4 sdk summary: ALL-PASS (${PASS} checks) ==="
else
  echo "=== t4 sdk summary: FAIL (${PASS} passed, ${FAIL} failed) ==="
  echo "--- last build log tail ---"; tail -20 "/tmp/t4-build-myApp.log" 2>/dev/null
  tail -20 "$WORK/MyApp/obj/Release/net10.0"/*.log 2>/dev/null
fi
