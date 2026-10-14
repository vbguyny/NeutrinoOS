#!/bin/bash
# M4 hosting-layer probe (docs/KESTREL-PORT.md M4).
# Boots the deploy image (/root/run.img), launches the hosted sample web
# app DLL from /apps/webapp via `webhost ... &`, and drives the matrix
# from the WSL host through slirp hostfwd (18090 -> 8090) plus guest-side
# status/stop checks. Prints PASS/FAIL per check + summary.
set -u
cd /root/neutrino
echo "image: $(md5sum /root/run.img | cut -c1-8)"
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
sleep 1
cp -f /root/run.img /tmp/m4.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-m4.fd
rm -f /root/qin-m4 /root/m4.out
touch /root/qin-m4
setsid bash -c "tail -f /root/qin-m4 | qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-m4.fd \
  -drive format=raw,file=/tmp/m4.img \
  -netdev user,id=n0,hostfwd=tcp::18090-:8090 \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -vga std -display none -serial stdio -no-reboot -no-shutdown" < /dev/null > /root/m4.out 2>&1 &

for i in $(seq 1 320); do
  grep -aq 'Type .help.' /root/m4.out 2>/dev/null && break
  sleep 0.5
done
echo BOOTED

printf 'webhost /apps/webapp/webapp.dll --urls http://0.0.0.0:8090 &\n' >> /root/qin-m4
sleep 5

PASSED=0; FAILED=0
pass() { echo "PASS: $1"; PASSED=$((PASSED + 1)); }
fail() { echo "FAIL: $1"; FAILED=$((FAILED + 1)); }

B=http://127.0.0.1:18090
code() { curl -sS -m 15 -o /dev/null -w '%{http_code}' "$@" 2>/dev/null; }
body() { curl -sS -m 15 "$@" 2>/dev/null; }

echo "=== launch + logging ==="
grep -aq '\[webhost\] app started' /root/m4.out && pass "webhost launched the app DLL" || fail "webhost launch (see /root/m4.out)"
grep -aq 'starting on port 8090' /root/m4.out && pass "ILogger shim rendered the startup line" || fail "logger startup line"
grep -aq '\[webapp\] hosted cooperatively' /root/m4.out && pass "app registered as a hosted app" || fail "hosted registration line"

echo "=== HTTP matrix (host -> guest) ==="
r=$(body $B/)
case "$r" in *'"app":"webapp"'*'"style":"WebApplicationBuilder"'*'"milestone":"M4"'*) pass "GET / blurb (builder style)";; *) fail "GET / ($r)";; esac
r=$(body $B/health)
[ "$r" = '{"status":"ok"}' ] && pass "GET /health" || fail "GET /health ($r)"
r=$(body $B/urls)
[ "$r" = '{"port":8090}' ] && pass "GET /urls (--urls binding)" || fail "GET /urls ($r)"
r=$(body $B/di)
[ "$r" = '{"service":"AppInfo","value":"webapp-m4"}' ] && pass "GET /di (DI shim)" || fail "GET /di ($r)"
r=$(body $B/log)
case "$r" in *'starting on port 8090'*) pass "GET /log (ILogger LastMessage)";; *) fail "GET /log ($r)";; esac
[ "$(code $B/no/such/route)" = 404 ] && pass "unknown route -> 404" || fail "unknown route"
sh=$(curl -sS -m 15 -D - -o /dev/null $B/health 2>/dev/null | grep -c 'Server: NeutrinoOS')
[ "$sh" = 1 ] && pass "Server header from the WebHost engine" || fail "Server header"

echo "=== keep-alive + concurrency ==="
curl -sS -m 15 -o /dev/null $B/health 2>/dev/null &
P1=$!
curl -sS -m 15 -o /dev/null $B/urls 2>/dev/null &
P2=$!
wait $P1 && pass "parallel GET /health" || fail "parallel /health"
wait $P2 && pass "parallel GET /urls" || fail "parallel /urls"

echo "=== guest-side control ==="
printf 'webhost status\n' >> /root/qin-m4
sleep 3
grep -aq 'hosted apps: 1' /root/m4.out && pass "webhost status shows 1 hosted app" || fail "webhost status"

printf 'webhost stop\n' >> /root/qin-m4
sleep 4
[ "$(code $B/health)" != 200 ] && pass "webhost stop stopped the app (curl no longer served)" || fail "stop: still serving"
grep -aq 'hosted apps: 0' /root/m4.out || true

echo "=== summary ==="
echo "PASSED=$PASSED FAILED=$FAILED"
echo "boot tests: $(grep -a -e 'JITTest] Passed' -e 'JITTest] Failed' /root/m4.out | tr '\n' ' ')"
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
exit $([ "$FAILED" = 0 ] && echo 0 || echo 1)
