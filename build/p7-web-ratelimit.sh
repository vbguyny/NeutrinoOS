#!/bin/bash
# Phase 7: webhost request-rate-limit (HTTP 429) acceptance probe.
#
# 1. Deploy + boot the serve image, start dhcp + webhost from the console.
# 2. Sanity: a single GET /health returns 200.
# 3. Burst: ~45 fast sequential GETs; the per-IP limit (30/s) must
#    produce at least one HTTP 429 response.
# 4. After a short pause the limiter window resets and GETs succeed again.
#
# Prints PASS/FAIL lines; exit code 0 when every check passed.
set -u
cd /root/neutrino
exec < /dev/null
export MTOOLS_SKIP_CHECK=1
echo "[$(date +%T)] p7-web-ratelimit start"
pkill -9 qemu-system 2>/dev/null || true
sleep 1

FAIL=0
ok()   { echo "PASS $1"; }
bad()  { echo "FAIL $1"; FAIL=1; }

cleanup() {
  pkill -9 -f qemu-system 2>/dev/null || true
  rm -f /root/qin
}
trap cleanup EXIT

echo "[$(date +%T)] deploying image..."
bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-deploy.sh > /root/p7web-deploy.log 2>&1
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-image-extras.sh >> /root/p7web-deploy.log 2>&1

# Probe-local webhost config: small per-IP rate limit so the 429 check is
# deterministic. (The shipped/fixture defaults stay at 30 req/s so the
# Phase 6 web tests run unthrottled.)
cat > /root/p7-webhost-config <<'EOF'
# NeutrinoOS webhost configuration (key=value)
Port=80
HttpsPort=443
MaxConnectionsPerIp=8
MaxRequestsPerIpPerSecond=3
EOF
timeout -s KILL 20 mcopy -i /root/run.img -o /root/p7-webhost-config ::/etc/webhost.conf

sleep 1
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin /root/p7web.log
mkfifo /root/qin

echo "[$(date +%T)] launching QEMU..."
( tail -f /root/qin | timeout 240 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file=/root/run.img \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -netdev user,id=n0,hostfwd=tcp::2222-:22,hostfwd=tcp::8080-:80,hostfwd=tcp::8444-:443 \
  -device virtio-net-pci,netdev=n0,disable-legacy=on \
  -display none -serial stdio -no-reboot -no-shutdown > /root/p7web.log 2>&1 ) &

echo "[$(date +%T)] waiting for shell..."
for i in $(seq 1 90); do
  if strings /root/p7web.log 2>/dev/null | grep -q 'neutrinoos> '; then echo "[$(date +%T)] prompt at ${i}s"; break; fi
  sleep 1
done
sleep 2

send() {
  timeout 5 bash -c "printf '%s\n' '$1' > /root/qin" 2>/dev/null || echo "[$(date +%T)] !! guest input fifo blocked"
  sleep "$2"
}

echo "[$(date +%T)] starting dhcp + webhost..."
send "dhcp" 12
send "webhost" 4

echo "[$(date +%T)] waiting for port 8080..."
UP=0
for i in $(seq 1 40); do
  if (exec 3<>/dev/tcp/127.0.0.1/8080) 2>/dev/null; then exec 3>&- 2>/dev/null; UP=1; break; fi
  sleep 1
done
[ $UP -eq 1 ] || bad "web listener reachable"

echo "[$(date +%T)] [1/3] sanity GET..."
CODE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 http://127.0.0.1:8080/health)
[ "$CODE" = "200" ] && ok "single request returns 200" || bad "single request returns 200 (got $CODE)"

echo "[$(date +%T)] [2/3] burst of 12 requests (fixture limit: 3/s)..."
GOT429=0
SUCCESS=0
for i in $(seq 1 12); do
  C=$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 http://127.0.0.1:8080/health 2>/dev/null)
  if [ "$C" = "429" ]; then GOT429=$((GOT429+1)); fi
  if [ "$C" = "200" ]; then SUCCESS=$((SUCCESS+1)); fi
done
echo "  burst results: 200x$SUCCESS 429x$GOT429 (other: $((12-SUCCESS-GOT429)))"
[ $GOT429 -gt 0 ] && ok "burst produced 429 Too Many Requests" || bad "burst produced 429 Too Many Requests"
[ $SUCCESS -gt 0 ] && ok "burst still served allowed requests" || bad "burst still served allowed requests"

echo "[$(date +%T)] [3/3] limiter recovers after the window..."
sleep 2
CODE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 http://127.0.0.1:8080/health)
[ "$CODE" = "200" ] && ok "recovery request returns 200" || bad "recovery request returns 200 (got $CODE)"

echo "=== summary: $([ $FAIL -eq 0 ] && echo ALL-PASS || echo HAS-FAILURES) ($(date +%T))"
exit $FAIL
