#!/bin/bash
# Task 5 ecosystem acceptance: official key, package templates (pack +
# verify every manifest), community docs and website assets.
set -uo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
WORK=/tmp/t5eco
PASS=0
FAIL=0
check() { if [ "$2" -eq 0 ]; then echo "PASS: $1"; PASS=$((PASS+1)); else echo "FAIL: $1"; FAIL=$((FAIL+1)); fi; }

if [ ! -x /root/p8sdk/npkg-host ]; then bash "$SRC/build/p8-sdk-build.sh" > /tmp/t5-npkg.log 2>&1; fi
test -x /root/p8sdk/npkg-host
check "npkg-host available" $?
NPKG=/root/p8sdk/npkg-host

echo "--- 1. official project key ---"
KEYS=$SRC/keys
test -f "$KEYS/private.key" && test -f "$KEYS/public.key"
check "keys/private.key + public.key present" $?
FPR=$($NPKG fingerprint "$KEYS/public.key")
[ "$FPR" = "5da1ed4fca245a0a08db88a42b187583a8eabc640f8a7eb129111f0fc7e1967b" ]
check "public key fingerprint matches keys/README.md ($FPR)" $?
FPR2=$($NPKG fingerprint "$KEYS/private.key" 2>/dev/null || true)
grep -aq "5da1ed4f" "$KEYS/README.md"
check "fingerprint documented in keys/README.md" $?

echo "--- 2. package templates pack + verify ---"
rm -rf "$WORK"; mkdir -p "$WORK"
pack_template() {
  local dir="$1" dll="$2"
  local out="$WORK/$(basename "$dir").npkg"
  mkdir -p "$WORK/$(basename "$dir")"
  cp "$SRC/templates/package-templates/$dir/manifest.json" "$WORK/$(basename "$dir")/"
  printf 'FAKE-DLL-FOR-PACK-TEST\n' > "$WORK/$(basename "$dir")/$dll"
  ( cd "$WORK/$(basename "$dir")" && $NPKG pack --manifest manifest.json --payload-dir . --out "$out" ) > "$WORK/pack-$dir.log" 2>&1
  $NPKG verify "$out" >> "$WORK/pack-$dir.log" 2>&1
}
for t in application utility library web-app; do
  case $t in
    application) dll=helloapp.dll ;;
    utility)     dll=hellotool.dll ;;
    library)     dll=hellolib.dll ;;
    web-app)     dll=helloweb.dll ;;
  esac
  pack_template "$t" "$dll" && [ -f "$WORK/$t.npkg" ]
  check "package template '$t' packs + verifies" $?
done
# driver template uses the driver manifest shape
mkdir -p "$WORK/driver"; cp "$SRC/templates/package-templates/driver/manifest.json" "$WORK/driver/"
printf 'FAKE-DLL\n' > "$WORK/driver/hellodrv.dll"
( cd "$WORK/driver" && $NPKG pack --manifest manifest.json --payload-dir . --out "$WORK/driver.npkg" ) > "$WORK/pack-driver.log" 2>&1
$NPKG verify "$WORK/driver.npkg" >> "$WORK/pack-driver.log" 2>&1
check "package template 'driver' packs + verifies" $?
$NPKG list "$WORK/driver.npkg" | grep -aq 'hellodrv.dll'
check "driver template payload is hellodrv.dll" $?

echo "--- 3. community docs ---"
check "CONTRIBUTING.md"      $([ -f "$SRC/CONTRIBUTING.md" ] && echo 0 || echo 1)
check "CODE_OF_CONDUCT.md"   $([ -f "$SRC/CODE_OF_CONDUCT.md" ] && echo 0 || echo 1)
check "docs/COMMUNITY-GUIDE.md"    $([ -f "$SRC/docs/COMMUNITY-GUIDE.md" ] && echo 0 || echo 1)
check "docs/PACKAGE-GUIDELINES.md" $([ -f "$SRC/docs/PACKAGE-GUIDELINES.md" ] && echo 0 || echo 1)
grep -aq 'reverse-DNS' "$SRC/docs/PACKAGE-GUIDELINES.md" && grep -aq 'Ed25519' "$SRC/docs/PACKAGE-GUIDELINES.md"
check "guidelines cover naming + signing" $?

echo "--- 4. website ---"
for f in index.html style.css config.js README.md; do
  test -f "$SRC/website/$f"
  check "website/$f" $?
done
grep -aq 'repository.json' "$SRC/website/index.html"
check "website fetches repository.json" $?

if [ "$FAIL" -eq 0 ]; then
  echo "=== t5 ecosystem summary: ALL-PASS (${PASS} checks) ==="
else
  echo "=== t5 ecosystem summary: FAIL (${PASS} passed, ${FAIL} failed) ==="
  for f in "$WORK"/pack-*.log; do echo "--- $f"; tail -5 "$f"; done
fi
