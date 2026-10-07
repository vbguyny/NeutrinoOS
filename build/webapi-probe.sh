#!/bin/bash
# WebAPI sample verification probe (docs/samples/webapi, Kestrel port M1).
# Boots the CLI image, starts the WebApplication-style `webapi` app (port
# 8080) and runs the full CRUD matrix from the WSL host through slirp
# hostfwd (18080 -> 8080), plus guest-side start/stop/status checks.
# Prints PASS/FAIL per check + summary.
set -u
cd /root/neutrino
echo "image: $(md5sum build/x64/neutrinoos-cli.img | cut -c1-8)"
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
sleep 1
cp -f build/x64/neutrinoos-cli.img /tmp/webapi.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-webapi.fd
rm -f /root/qin-webapi /root/webapi.out
touch /root/qin-webapi
setsid bash -c "tail -f /root/qin-webapi | qemu-system-x86_64 -machine q35 -m 2G -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-webapi.fd \
  -drive format=raw,file=/tmp/webapi.img \
  -netdev user,id=n0,hostfwd=tcp::18080-:8080 \
  -device virtio-net-pci,netdev=n0 \
  -vga std -display none -serial stdio -no-reboot -no-shutdown" < /dev/null > /root/webapi.out 2>&1 &

for i in $(seq 1 120); do
  grep -aq 'Type .help.' /root/webapi.out 2>/dev/null && break
  sleep 0.5
done
echo BOOTED
printf 'webapi start\n' >> /root/qin-webapi
sleep 4

PASSED=0; FAILED=0
pass() { echo "PASS: $1"; PASSED=$((PASSED + 1)); }
fail() { echo "FAIL: $1"; FAILED=$((FAILED + 1)); }

B=http://127.0.0.1:18080
code() { curl -sS -m 15 -o /dev/null -w '%{http_code}' "$@" 2>/dev/null; }
body() { curl -sS -m 15 "$@" 2>/dev/null; }

echo "=== discovery + info ==="
r=$(body $B/)
case "$r" in *'"service":"NeutrinoOS WebAPI sample"'*'"style"'*'"endpoints"'*) pass "GET / blurb JSON (WebApplication style)";; *) fail "GET / ($r)";; esac
r=$(body $B/api/v1/info)
case "$r" in *'"service":"webapi"'*'"version":"1.0.0"'*'"uptime_s"'*) pass "GET /info";; *) fail "GET /info ($r)";; esac
[ "$(code $B/api/v1/info)" = 200 ] && pass "GET /info -> 200" || fail "GET /info code"
sh=$(curl -sS -m 15 -D - -o /dev/null $B/api/v1/info 2>/dev/null | grep -c 'Server: NeutrinoOS')
[ "$sh" = 1 ] && pass "Server header from the WebHost engine" || fail "Server header"

echo "=== echo ==="
r=$(body "$B/api/v1/echo?msg=hello")
[ "$r" = '{"method":"GET","msg":"hello"}' ] && pass "GET /echo?msg=hello" || fail "GET /echo ($r)"
r=$(body "$B/api/v1/echo")
[ "$r" = '{"method":"GET","msg":""}' ] && pass "GET /echo (no msg)" || fail "GET /echo no-msg ($r)"
printf 'ping-pong' > /tmp/webapi-body.txt
r=$(body -X POST --data-binary @/tmp/webapi-body.txt $B/api/v1/echo)
case "$r" in *'"content_length":9'*'"you_sent":"ping-pong"'*) pass "POST /echo body";; *) fail "POST /echo ($r)";; esac

echo "=== tasks CRUD ==="
printf '{"name":"write docs"}' > /tmp/webapi-task1.txt
r=$(body -X POST -H 'Content-Type: application/json' --data-binary @/tmp/webapi-task1.txt $B/api/v1/tasks)
[ "$r" = '{"id":1,"name":"write docs"}' ] && pass "POST task JSON -> id 1" || fail "POST task JSON ($r)"
[ "$(code -X POST -d name=secondtask $B/api/v1/tasks)" = 201 ] && pass "POST task form -> 201" || fail "POST task form"
r=$(body $B/api/v1/tasks)
case "$r" in *'"count":2'*'"name":"write docs"'*'"name":"secondtask"'*) pass "GET /tasks lists both";; *) fail "GET /tasks ($r)";; esac
r=$(body $B/api/v1/tasks/1)
[ "$r" = '{"id":1,"name":"write docs"}' ] && pass "GET /tasks/1 (route value {id})" || fail "GET /tasks/1 ($r)"
printf '{"name":"ship docs"}' > /tmp/webapi-task2.txt
r=$(body -X PUT --data-binary @/tmp/webapi-task2.txt $B/api/v1/tasks/1)
[ "$r" = '{"id":1,"name":"ship docs"}' ] && pass "PUT /tasks/1 renames" || fail "PUT /tasks/1 ($r)"
r=$(body -X DELETE $B/api/v1/tasks/1)
[ "$r" = '{"deleted":1}' ] && pass "DELETE /tasks/1" || fail "DELETE /tasks/1 ($r)"
[ "$(code $B/api/v1/tasks/1)" = 404 ] && pass "GET deleted task -> 404" || fail "GET deleted task"
[ "$(code -X DELETE $B/api/v1/tasks/99)" = 404 ] && pass "DELETE missing task -> 404" || fail "DELETE missing"
[ "$(code -X POST $B/api/v1/tasks)" = 400 ] && pass "POST no name -> 400" || fail "POST no name"
[ "$(code -X PUT -d name=x $B/api/v1/tasks/42)" = 404 ] && pass "PUT missing task -> 404" || fail "PUT missing"
[ "$(code -X PATCH $B/api/v1/tasks)" = 405 ] && pass "PATCH collection -> 405 (route table)" || fail "PATCH collection"
[ "$(code -X PATCH $B/api/v1/tasks/2)" = 405 ] && pass "PATCH item -> 405 (route table)" || fail "PATCH item"
[ "$(code $B/no/such/route)" = 404 ] && pass "unknown route -> 404" || fail "unknown route"
[ "$(code $B/api/v1/tasks/abc)" = 404 ] && pass "non-numeric id -> 404 (app validation)" || fail "non-numeric id"

echo "=== concurrency + counters ==="
curl -sS -m 15 -o /dev/null $B/api/v1/info 2>/dev/null &
P1=$!
curl -sS -m 15 -o /dev/null $B/api/v1/tasks 2>/dev/null &
P2=$!
wait $P1 && pass "parallel GET /info" || fail "parallel GET /info"
wait $P2 && pass "parallel GET /tasks" || fail "parallel GET /tasks"
r=$(body $B/api/v1/info)
case "$r" in *'"requests":'*) pass "info counters present";; *) fail "info counters ($r)";; esac

echo "=== guest-side control ==="
printf 'webapi status\n' >> /root/qin-webapi
sleep 2
grep -aq '\[webapi\] running' /root/webapi.out && pass "webapi status: running" || fail "webapi status"
printf 'webapi stop\n' >> /root/qin-webapi
sleep 2
grep -aq '\[webapi\] service stopped' /root/webapi.out && pass "webapi stop" || fail "webapi stop"
[ "$(code -m 5 $B/api/v1/info)" != 200 ] && pass "server down after stop" || fail "server still up after stop"
printf 'webapi start\n' >> /root/qin-webapi
sleep 4
[ "$(code $B/api/v1/info)" = 200 ] && pass "restart works" || fail "restart"
printf 'webapi status\n' >> /root/qin-webapi
sleep 2
grep -aq '\[webapi\] running' /root/webapi.out && pass "status after restart: running" || fail "status after restart"

echo
echo "===================="
echo "PASSED=$PASSED FAILED=$FAILED"
echo "===================="
tail -5 /root/webapi.out
pkill -9 -f '[q]emu-system-x86_64' 2>/dev/null || true
