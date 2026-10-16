#!/bin/bash
# M8 NuGet third-party-library probe (specs/future-todos.md: "Test that
# nugets/third party libraries can be used with NeutrinoOS (do not support
# anything less than .NET 10)").
#
# The claim under test: the SAME Program.cs (src/apps/libapi) runs on
# Windows/.NET 10 and on NeutrinoOS, using REAL NuGet packages restored from
# the package archive: Microsoft.Extensions.ObjectPool 10.0.13 and
# Microsoft.Extensions.Primitives 10.0.13 (both with lib/net10.0 assets).
# Phase A builds/runs the Windows twin (samples/nuget-compat/libapi.windows)
# with the actual packages and captures its responses; Phase B boots the CLI
# image (which carries the package DLLs in ::/lib, loaded on demand by
# TryLoadAssemblyFromLib), launches the device app DLL from /apps/webapp via
# `webhost ... &` and byte-compares the responses through slirp hostfwd
# (18098 -> 8098). Device requests may JIT-compile the Kestrel pipeline on
# first hit, so waits here are deliberately patient. Prints PASS/FAIL per
# check + summary.
set -u
cd /root/neutrino
echo "image: $(md5sum build/x64/neutrinoos-cli.img | cut -c1-8)"

PASSED=0; FAILED=0
pass() { echo "PASS: $1"; PASSED=$((PASSED + 1)); }
fail() { echo "FAIL: $1"; FAILED=$((FAILED + 1)); }
code() { curl -sS -m 20 -o /dev/null -w '%{http_code}' "$@" 2>/dev/null; }
body() { curl -sS -m 150 "$@" 2>/dev/null; }

echo "=== phase A: Windows twin (real Microsoft.Extensions.* NuGet packages) ==="
export PATH=/usr/share/dotnet:$PATH
TWIN=/mnt/d/Projects/Code/NeutrinoOS/samples/nuget-compat/libapi.windows
if (cd "$TWIN" && dotnet build -v q -nologo > /root/m8-twin-build.log 2>&1); then
  pass "twin builds the shared Program.cs against the NuGet packages"
else
  fail "twin build ($(grep -m1 error /root/m8-twin-build.log))"
fi

pkill -9 -f '[l]ibapi.dll' 2>/dev/null || true
rm -f /root/m8-twin.out
setsid bash -c "cd $TWIN && exec dotnet bin/Debug/net10.0/libapi.dll" < /dev/null > /root/m8-twin.out 2>&1 &
disown

TW=http://127.0.0.1:8098
UP=0
for i in $(seq 1 60); do
  [ "$(code -m 3 $TW/health)" = 200 ] && UP=1 && break
  sleep 0.5
done
[ "$UP" = 1 ] && pass "twin listening on 8098" || fail "twin start (see /root/m8-twin.out)"

body $TW/          > /root/m8-twin-root.out
body $TW/health    > /root/m8-twin-health.out
body $TW/strings   > /root/m8-twin-strings.out
body $TW/pool      > /root/m8-twin-pool.out
[ "$(code $TW/nope)" = 404 ] && pass "twin unknown route -> 404" || fail "twin 404"

pkill -9 -f '[l]ibapi.dll' 2>/dev/null || true
echo "twin: $(cat /root/m8-twin-root.out)"
echo "twin: $(cat /root/m8-twin-strings.out)"

echo "=== phase B: device (NeutrinoOS, package DLLs from ::/lib) ==="
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
sleep 1
cp -f build/x64/neutrinoos-cli.img /tmp/m8.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-m8.fd
rm -f /root/qin-m8p /root/m8p.out
touch /root/qin-m8p
setsid bash -c "tail -f /root/qin-m8p | qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-m8.fd \
  -drive format=raw,file=/tmp/m8.img \
  -netdev user,id=n0,hostfwd=tcp::18098-:8098 \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -vga std -display none -serial stdio -no-reboot -no-shutdown" < /dev/null > /root/m8p.out 2>&1 &
disown

for i in $(seq 1 160); do
  grep -aq 'Type .help.' /root/m8p.out 2>/dev/null && break
  sleep 0.5
done
echo BOOTED
printf 'webhost /apps/webapp/libapi.dll &\n' >> /root/qin-m8p

B=http://127.0.0.1:18098

# Wait for the server to accept connections (the listener probe hits /health).
UP=0
for i in $(seq 1 90); do
  [ "$(code -m 5 $B/health)" = 200 ] && UP=1 && break
  sleep 1
done
grep -aq 'listening on port 8098' /root/m8p.out && pass "device app bound 8098 (webhost launch)" || fail "device launch (see /root/m8p.out)"
[ "$UP" = 1 ] && pass "device /health answered" || fail "device never answered"

# Patient warm-up: first hits JIT-compile Kestrel request handling paths.
code $B/         > /dev/null 2>&1
code $B/health   > /dev/null 2>&1

body $B/          > /root/m8-dev-root.out
body $B/health    > /root/m8-dev-health.out
body $B/strings   > /root/m8-dev-strings.out
body $B/pool      > /root/m8-dev-pool.out

echo "=== byte parity (device vs Windows twin) ==="
cmp -s /root/m8-dev-root.out    /root/m8-twin-root.out    && pass "GET / same bytes"       || fail "GET / differs ($(cat /root/m8-dev-root.out) vs $(cat /root/m8-twin-root.out))"
cmp -s /root/m8-dev-health.out  /root/m8-twin-health.out  && pass "GET /health same bytes" || fail "GET /health differs ($(cat /root/m8-dev-health.out))"
cmp -s /root/m8-dev-strings.out /root/m8-twin-strings.out && pass "GET /strings same bytes (StringValues+StringSegment via MemoryExtensions)" || fail "GET /strings differs ($(cat /root/m8-dev-strings.out) vs $(cat /root/m8-twin-strings.out))"
cmp -s /root/m8-dev-pool.out    /root/m8-twin-pool.out    && pass "GET /pool same bytes (DefaultObjectPool reused buffer)" || fail "GET /pool differs ($(cat /root/m8-dev-pool.out) vs $(cat /root/m8-twin-pool.out))"

echo "=== device HTTP details ==="
[ "$(code $B/nope)" = 404 ] && pass "device unknown route -> 404" || fail "device 404"
ct=$(curl -sS -m 20 -o /dev/null -w '%{content_type}' $B/health 2>/dev/null)
case "$ct" in application/json*) pass "device content-type application/json";; *) fail "device content-type ($ct)";; esac
sh=$(curl -sS -m 20 -D - -o /dev/null $B/health 2>/dev/null | grep -c 'Server: NeutrinoOS')
[ "$sh" = 1 ] && pass "device Server header" || fail "device Server header"

echo "=== package DLLs loaded on device ==="
grep -aq 'Loaded referenced assembly from /lib: Microsoft.Extensions.ObjectPool' /root/m8p.out && pass "device loaded Microsoft.Extensions.ObjectPool from /lib" || fail "ObjectPool load not seen"
grep -aq 'Loaded referenced assembly from /lib: Microsoft.Extensions.Primitives' /root/m8p.out && pass "device loaded Microsoft.Extensions.Primitives from /lib" || fail "Primitives load not seen"

echo
echo "===================="
echo "PASSED=$PASSED FAILED=$FAILED"
echo "===================="

tail -3 /root/m8p.out
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
exit $([ "$FAILED" = 0 ] && echo 0 || echo 1)
