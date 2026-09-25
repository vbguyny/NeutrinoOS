#!/bin/bash
# Phase 8: build the host-side npkg CLI (sdk/npkg) inside WSL.
#
#   bash build/p8-sdk-build.sh
#
# Output: /root/p8sdk/npkg-host (+ npkg-host.dll and dependencies).
set -euo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

OUT=${P8_SDK_OUT:-/root/p8sdk}

rm -rf "$OUT"
cd /mnt/d/Projects/Code/NeutrinoOS/sdk/npkg
dotnet build Npkg.Cli.csproj -c Release -o "$OUT" --nologo -v q

echo "=== SDK BUILD OK ==="
ls "$OUT"/npkg-host*
