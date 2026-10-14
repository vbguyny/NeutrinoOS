#!/bin/bash
# M5 transport probe (docs/KESTREL-PORT.md M5).
# Boots the deploy image, launches the hosted sample web app (HTTP 8090 +
# HTTPS 8091 on the same engine, TLS 1.3) and checks from the WSL host
# through slirp hostfwd (18090->8090, 18091->8091):
#   - HTTPS with HTTP/1.1 over TLS 1.3 (curl -k)
#   - HTTP/2 over TLS via ALPN (curl -k --http2, http_version=2)
#   - HTTP/2 cleartext, prior knowledge (curl --http2-prior-knowledge)
#   - the IHostApplicationLifetime hook firing in the guest log
#   - the M6 OpenAPI document endpoint over HTTP/1.1 and h2c
# Prints PASS/FAIL per check + summary.
set -u
cd /root/neutrino
echo "image: $(md5sum /root/run.img | cut -c1-8)"
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
sleep 1
cp -f /root/run.img /tmp/m5.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-m5.fd
rm -f /root/qin-m5 /root/m5.out
touch /root/qin-m5
setsid bash -c "tail -f /root/qin-m5 | qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-m5.fd \
  -drive format=raw,file=/tmp/m5.img \
  -netdev user,id=n0,hostfwd=tcp::18090-:8090,hostfwd=tcp::18091-:8091 \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -vga std -display none -serial stdio -no-reboot -no-shutdown" < /dev/null > /root/m5.out 2>&1 &

for i in $(seq 1 320); do
  grep -aq 'Type .help.' /root/m5.out 2>/dev/null && break
  sleep 0.5
done
echo BOOTED

printf 'webhost /apps/webapp/webapp.dll --urls http://0.0.0.0:8090 &\n' >> /root/qin-m5
sleep 5

PASSED=0; FAILED=0
pass() { echo "PASS: $1"; PASSED=$((PASSED + 1)); }
fail() { echo "FAIL: $1"; FAILED=$((FAILED + 1)); }

H=http://127.0.0.1:18090
S=https://127.0.0.1:18091
code() { curl -sS -m 20 -o /dev/null -w '%{http_code}' "$@" 2>/dev/null; }
body() { curl -sS -m 20 "$@" 2>/dev/null; }
ver()  { curl -sS -m 20 -o /dev/null -w '%{http_version}' "$@" 2>/dev/null; }

echo "=== launch ==="
grep -aq '\[webhost\] app started' /root/m5.out && pass "webhost launched the app DLL" || fail "webhost launch"
grep -aq '(https, TLS 1.3)' /root/m5.out && pass "HTTPS listener bound (TLS 1.3)" || fail "https listener line"
grep -aq 'lifetime: ApplicationStarted hook fired' /root/m5.out && pass "IHostApplicationLifetime hook fired" || fail "lifetime hook line"

echo "=== HTTPS (HTTP/1.1 over TLS 1.3) ==="
r=$(body -k $S/health)
[ "$r" = '{"status":"ok"}' ] && pass "GET https /health" || fail "https /health ($r)"
r=$(body -k $S/lifetime)
[ "$r" = '{"started":true,"stopping":false}' ] && pass "GET https /lifetime (started hook state)" || fail "https /lifetime ($r)"
[ "$(code -k $S/di)" = 200 ] && pass "https /di -> 200" || fail "https /di"
[ "$(code -k $S/no/such)" = 404 ] && pass "https unknown route -> 404" || fail "https 404"

echo "=== HTTP/2 over TLS (ALPN) ==="
v=$(ver -k --http2 $S/health)
[ "$v" = "2" ] && pass "curl --http2 negotiated h2 over TLS (http_version=2)" || fail "h2 over TLS ($v)"
r=$(body -k --http2 $S/urls)
[ "$r" = '{"port":8090}' ] && pass "h2 GET /urls body" || fail "h2 /urls ($r)"
[ "$(code -k --http2 $S/log)" = 200 ] && pass "h2 GET /log -> 200" || fail "h2 /log"

echo "=== HTTP/2 cleartext (prior knowledge) ==="
v=$(ver --http2-prior-knowledge $H/health)
[ "$v" = "2" ] && pass "h2c prior knowledge (http_version=2)" || fail "h2c ($v)"
r=$(body --http2-prior-knowledge $H/)
case "$r" in *'"app":"webapp"'*) pass "h2c GET / blurb";; *) fail "h2c / ($r)";; esac
[ "$(code --http2-prior-knowledge $H/no/such)" = 404 ] && pass "h2c unknown route -> 404" || fail "h2c 404"

echo "=== HTTP/1.1 regression (unchanged path) ==="
r=$(body $H/health)
[ "$r" = '{"status":"ok"}' ] && pass "plain http /health still works" || fail "http /health ($r)"

echo "=== OpenAPI document (M6) ==="
r=$(body $H/openapi/v1.json)
case "$r" in
  *'"openapi": "3.1.0"'*'"title": "NeutrinoOS webapp"'*'"/di"'*) pass "GET /openapi/v1.json (OpenAPI 3.1 doc for the routes)";;
  *) fail "openapi doc ($r)";;
esac
ct=$(curl -sS -m 20 -o /dev/null -w '%{content_type}' $H/openapi/v1.json 2>/dev/null)
case "$ct" in application/json*) pass "openapi content-type application/json";; *) fail "openapi content-type ($ct)";; esac
[ "$(code --http2-prior-knowledge $H/openapi/v1.json)" = 200 ] && pass "h2c serves the document" || fail "h2c openapi"
jerr=$(body $H/openapi/v1.json | python3 -m json.tool 2>&1 >/dev/null | tail -1)
[ -z "$jerr" ] && pass "openapi document is valid JSON (json.tool)" || fail "openapi JSON invalid ($jerr)"

echo "=== summary ==="
echo "PASSED=$PASSED FAILED=$FAILED"
echo "boot tests: $(grep -a -e 'JITTest] Passed' -e 'JITTest] Failed' /root/m5.out | tr '\n' ' ')"
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
exit $([ "$FAILED" = 0 ] && echo 0 || echo 1)
