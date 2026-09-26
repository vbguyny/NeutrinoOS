#!/bin/bash
# Task 4 repo-server e2e: build the server, publish two packages into a
# repo directory, serve it over HTTP, fetch index/signature/public key/
# package and assert integrity + byte-parity with the host npkg CLI.
set -uo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
BIN=/root/t4repo-bin
REPO=/root/t4repo
KEYS=/root/t4keys
FETCH=/tmp/t4fetch
PORT=8099
PASS=0
FAIL=0
check() { if [ "$2" -eq 0 ]; then echo "PASS: $1"; PASS=$((PASS+1)); else echo "FAIL: $1"; FAIL=$((FAIL+1)); fi; }

# --- prereqs: npkg-host + a couple of built packages ---------------------
if [ ! -x /root/p8sdk/npkg-host ]; then bash "$SRC/build/p8-t4-sdk-e2e.sh" > /tmp/t4-prereq.log 2>&1; fi
test -x /root/p8sdk/npkg-host
check "npkg-host present" $?

APP=/root/t4sdk/MyApp/bin/Release/net10.0/MyApp.npkg
UTIL=/root/t4sdk/mytool/bin/Release/net10.0/mytool.npkg
if [ ! -f "$APP" ] || [ ! -f "$UTIL" ]; then
  bash "$SRC/build/p8-t4-sdk-e2e.sh" > /tmp/t4-prereq.log 2>&1 || true
fi
test -f "$APP" && test -f "$UTIL"
check "test packages available (MyApp, mytool)" $?

# --- keys ----------------------------------------------------------------
rm -rf "$KEYS"
/root/p8sdk/npkg-host keygen --out-dir "$KEYS" --force > /tmp/t4-keygen.log 2>&1
test -f "$KEYS/private.key" && test -f "$KEYS/public.key"
check "keygen wrote private.key + public.key" $?

# --- repo dir + server ---------------------------------------------------
rm -rf "$REPO" "$FETCH" "$BIN"; mkdir -p "$REPO" "$FETCH"
cp "$APP" "$REPO/"
cp "$UTIL" "$REPO/"

dotnet build "$SRC/sdk/repo-server/Npkg.RepoServer.csproj" -c Release -o "$BIN" > /tmp/t4-repobuild.log 2>&1
test -x "$BIN/npkg-repo-server"
check "repo server built" $?

"$BIN/npkg-repo-server" --dir "$REPO" --port $PORT --key "$KEYS/private.key" --name "testrepo" --quiet > /tmp/t4-server.log 2>&1 &
SRV=$!
for i in $(seq 1 20); do
  curl -s -o /dev/null "http://127.0.0.1:$PORT/repository.json" && break
  sleep 0.5
done

curl -s "http://127.0.0.1:$PORT/repository.json" -o "$FETCH/repository.json"
check "fetched /repository.json" $?
grep -aq 'MyApp' "$FETCH/repository.json" && grep -aq 'mytool' "$FETCH/repository.json" && grep -aq 'npkg-repo/1' "$FETCH/repository.json"
check "index lists both packages (npkg-repo/1)" $?

curl -s "http://127.0.0.1:$PORT/repository.json.sig" -o "$FETCH/repository.json.sig"
SIGSZ=$(stat -c%s "$FETCH/repository.json.sig")
[ "$SIGSZ" = "64" ]
check "signature is 64 bytes" $?

curl -s "http://127.0.0.1:$PORT/repo.pub" -o "$FETCH/repo.pub"
PUBHEX=$(tr -d '\n' < "$FETCH/repo.pub" | wc -c)
[ "$PUBHEX" = "64" ]
check "repo.pub is 64 hex chars" $?

curl -s "http://127.0.0.1:$PORT/MyApp.npkg" -o "$FETCH/MyApp.npkg"
cmp -s "$FETCH/MyApp.npkg" "$APP"
check "package download byte-identical" $?

curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/nope.npkg" > "$FETCH/404.txt"
grep -q '404' "$FETCH/404.txt"
check "missing file -> 404" $?

# --- parity with the host npkg CLI ---------------------------------------
/root/p8sdk/npkg-host repo-index --dir "$REPO" --key "$KEYS/private.key" --name "testrepo" > /tmp/t4-repoindex.log 2>&1
cmp -s "$REPO/repository.json" "$FETCH/repository.json"
check "server index == npkg-host repo-index (byte-identical)" $?
cmp -s "$REPO/repository.json.sig" "$FETCH/repository.json.sig"
check "server signature == npkg-host signature" $?

kill $SRV 2>/dev/null || true
pkill -9 -f npkg-repo-server 2>/dev/null || true

if [ "$FAIL" -eq 0 ]; then
  echo "=== t4 repo summary: ALL-PASS (${PASS} checks) ==="
else
  echo "=== t4 repo summary: FAIL (${PASS} passed, ${FAIL} failed) ==="
  echo "--- server log ---"; tail -10 /tmp/t4-server.log 2>/dev/null
  echo "--- build log tail ---"; tail -10 /tmp/t4-repobuild.log 2>/dev/null
fi
