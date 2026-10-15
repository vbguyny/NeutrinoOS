#!/bin/bash
# M7 ASP.NET Core namespace-compat probe (docs/KESTREL-PORT.md M7).
#
# The claim under test: the SAME Program.cs (src/apps/helloapi) runs on
# Windows/ASP.NET Core and on NeutrinoOS. Phase A builds and runs the
# Windows twin (samples/aspnet-compat/helloapi.windows) with the real
# Microsoft.AspNetCore.App and captures its responses; Phase B boots the
# CLI image, launches the device app DLL from /apps/webapp via
# `webhost ... &` and byte-compares the responses through slirp hostfwd
# (18092 -> 8092). Prints PASS/FAIL per check + summary.
set -u
cd /root/neutrino
echo "image: $(md5sum build/x64/neutrinoos-cli.img | cut -c1-8)"

PASSED=0; FAILED=0
pass() { echo "PASS: $1"; PASSED=$((PASSED + 1)); }
fail() { echo "FAIL: $1"; FAILED=$((FAILED + 1)); }
code() { curl -sS -m 15 -o /dev/null -w '%{http_code}' "$@" 2>/dev/null; }
body() { curl -sS -m 15 "$@" 2>/dev/null; }

echo "=== phase A: Windows twin (real ASP.NET Core) ==="
export PATH=/usr/share/dotnet:$PATH
TWIN=/mnt/d/Projects/Code/NeutrinoOS/samples/aspnet-compat/helloapi.windows
if (cd "$TWIN" && dotnet build -v q -nologo > /root/m7-twin-build.log 2>&1); then
  pass "twin builds the shared Program.cs against Microsoft.AspNetCore.App"
else
  fail "twin build ($(grep -m1 error /root/m7-twin-build.log))"
fi

pkill -9 -f '[h]elloapi' 2>/dev/null || true
rm -f /root/m7-twin.out
setsid bash -c "cd $TWIN && exec dotnet bin/Debug/net10.0/helloapi.dll" < /dev/null > /root/m7-twin.out 2>&1 &
disown

TW=http://127.0.0.1:8092
UP=0
for i in $(seq 1 40); do
  [ "$(code -m 3 $TW/)" = 200 ] && UP=1 && break
  sleep 0.5
done
[ "$UP" = 1 ] && pass "twin listening on 8092" || fail "twin start (see /root/m7-twin.out)"

body $TW/                        > /root/m7-twin-root.out
body $TW/health                  > /root/m7-twin-health.out
body $TW/greet/device            > /root/m7-twin-greet.out
body "$TW/echo?msg=hi"           > /root/m7-twin-echo.out
[ "$(code $TW/nope)" = 404 ] && pass "twin unknown route -> 404" || fail "twin 404"

pkill -9 -f '[h]elloapi' 2>/dev/null || true
echo "twin: $(cat /root/m7-twin-root.out)"

echo "=== phase B: device (NeutrinoOS DDK shim) ==="
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
sleep 1
cp -f build/x64/neutrinoos-cli.img /tmp/m7.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-m7.fd
rm -f /root/qin-m7 /root/m7.out
touch /root/qin-m7
setsid bash -c "tail -f /root/qin-m7 | qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-m7.fd \
  -drive format=raw,file=/tmp/m7.img \
  -netdev user,id=n0,hostfwd=tcp::18092-:8092 \
  -device virtio-net-pci,netdev=n0 \
  -vga std -display none -serial stdio -no-reboot -no-shutdown" < /dev/null > /root/m7.out 2>&1 &
disown

for i in $(seq 1 120); do
  grep -aq 'Type .help.' /root/m7.out 2>/dev/null && break
  sleep 0.5
done
echo BOOTED
printf 'webhost /apps/webapp/helloapi.dll &\n' >> /root/qin-m7
sleep 8

B=http://127.0.0.1:18092

grep -aq 'listening on port 8092' /root/m7.out && pass "device app bound 8092 (webhost launch)" || fail "device launch (see /root/m7.out)"

body $B/                        > /root/m7-dev-root.out
body $B/health                  > /root/m7-dev-health.out
body $B/greet/device            > /root/m7-dev-greet.out
body "$B/echo?msg=hi"           > /root/m7-dev-echo.out

echo "=== byte parity (device vs Windows twin) ==="
cmp -s /root/m7-dev-root.out   /root/m7-twin-root.out   && pass "GET / same bytes"      || fail "GET / differs ($(cat /root/m7-dev-root.out) vs $(cat /root/m7-twin-root.out))"
cmp -s /root/m7-dev-health.out /root/m7-twin-health.out && pass "GET /health same bytes" || fail "GET /health differs ($(cat /root/m7-dev-health.out))"
cmp -s /root/m7-dev-greet.out  /root/m7-twin-greet.out  && pass "GET /greet/device same bytes" || fail "GET /greet/device differs ($(cat /root/m7-dev-greet.out))"
cmp -s /root/m7-dev-echo.out   /root/m7-twin-echo.out   && pass "GET /echo?msg=hi same bytes" || fail "GET /echo differs ($(cat /root/m7-dev-echo.out))"

echo "=== device HTTP details ==="
[ "$(code $B/nope)" = 404 ] && pass "device unknown route -> 404" || fail "device 404"
ct=$(curl -sS -m 15 -o /dev/null -w '%{content_type}' $B/health 2>/dev/null)
case "$ct" in application/json*) pass "device content-type application/json";; *) fail "device content-type ($ct)";; esac
sh=$(curl -sS -m 15 -D - -o /dev/null $B/health 2>/dev/null | grep -c 'Server: NeutrinoOS')
[ "$sh" = 1 ] && pass "device Server header" || fail "device Server header"

echo
echo "===================="
echo "PASSED=$PASSED FAILED=$FAILED"
echo "===================="

tail -3 /root/m7.out
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
exit $([ "$FAILED" = 0 ] && echo 0 || echo 1)
