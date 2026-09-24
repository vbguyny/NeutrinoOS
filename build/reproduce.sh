#!/bin/bash
# Phase 7: reproducible-build check. Builds the image twice from a clean
# state and compares the checksums of the release artifacts.
set -eu
cd /root/neutrino

HASHFILE=/root/repro-hashes.txt

build_once() {
  rm -rf build/x64 src/korlib/obj src/korlib/bin
  export SOURCE_DATE_EPOCH=315532800
  export MTOOLS_SKIP_CHECK=1
  export DOTNET_ROOT=/usr/share/dotnet
  export PATH="/usr/share/dotnet:$PATH"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  export DOTNET_NOLOGO=1
  make image > /dev/null
}

echo "[reproduce] build 1/2..."
build_once
sha256sum build/x64/BOOTX64.EFI build/x64/neutrinoos.img | sed 's/ .*\// /' > $HASHFILE
cat $HASHFILE

echo "[reproduce] build 2/2..."
build_once
sha256sum build/x64/BOOTX64.EFI build/x64/neutrinoos.img | sed 's/ .*\// /' > $HASHFILE.2
cat $HASHFILE.2

if cmp -s $HASHFILE $HASHFILE.2; then
  echo "[reproduce] OK - artifacts are byte-identical across two clean builds"
  exit 0
else
  echo "[reproduce] MISMATCH:"
  diff $HASHFILE $HASHFILE.2 || true
  exit 1
fi
