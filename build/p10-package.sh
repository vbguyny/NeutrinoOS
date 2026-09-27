#!/bin/bash
# Phase 10 Task 5: pack neutrinoos.utils.exfat with the SDK npkg host
# tool into /root/p10npkg/repo, sign it with the test key, and build the
# signed repository index. The p10-npkg-deploy/test scripts consume the
# repo; the on-device leg proves `npkg install neutrinoos.utils.exfat`
# delivers working tools.
#
#   bash build/p10-package.sh
set -euo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
HOST=/root/p8sdk/npkg-host
WORK=/root/p10pkg
REPO=/root/p10npkg/repo
UTILBIN=/root/phase5bin
KEYS="$WORK/keys"

test -d "$UTILBIN" || { echo "missing $UTILBIN - run p5-apps-build.sh first"; exit 1; }

# ---- host CLI (shared with the Phase 8 tooling) ----
if [ ! -x "$HOST" ]; then
  bash "$SRC/build/p8-sdk-build.sh"
fi

# ---- stage payload (the four exFAT tool assemblies) ----
rm -rf "$WORK"
mkdir -p "$WORK/payload" "$WORK/keys" "$REPO"
rsync -a "$SRC/tests/npkg/keys/" "$WORK/keys/"
for tool in mkexfat fsck.exfat exfatlabel exfatattrib; do
  cp "$UTILBIN/$tool.dll" "$WORK/payload/$tool.dll"
done
cp "$SRC/packages/neutrinoos.utils.exfat/manifest.json" "$WORK/manifest.json"

# ---- pack + sign + index ----
"$HOST" pack --manifest "$WORK/manifest.json" --payload-dir "$WORK/payload" \
  --out "$REPO/neutrinoos.utils.exfat-1.0.0.npkg" --key "$KEYS/private.key"

"$HOST" verify "$REPO/neutrinoos.utils.exfat-1.0.0.npkg"

# Rebuild the index over just this package (Phase 8 repo-index semantics).
"$HOST" repo-index --dir "$REPO" --key "$KEYS/private.key" --name local

# Device trust anchor: the repo public key (deploy copies this into
# /etc/npkg/trusted-keys/).
cp "$KEYS/public.key" "$REPO/neutrinoos-utils.pub"

ls -la "$REPO"
echo "P10-PACKAGE: OK"
