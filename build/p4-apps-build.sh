#!/bin/bash
# Build all Phase 4 test apps: sync the Windows tests/phase4 tree into
# the WSL build tree, clean korlib's obj (its **/*.cs glob would pick up
# generated AssemblyInfo files), then build every app into
# /root/phase4apps.
set -u
WIN=/mnt/d/Projects/Code/NeutrinoOS
P4=/root/neutrino
OUT=/root/phase4apps
APPS="Hello/Hello FileIo/FileIo Linq/Linq Async/Async CSharp14/CSharp14 Interactive/Interactive Net/Net MultiLib/MultiLib P10Hello/P10Hello"

mkdir -p "$OUT" "$P4/tests/phase4"
rsync -a --delete --exclude obj --exclude bin "$WIN/tests/phase4/" "$P4/tests/phase4/"
# rsync's --exclude protects stale obj/ dirs from --delete; remove them so
# generated AssemblyInfo files are not compiled twice by custom globs.
find "$P4/tests/phase4" -type d -name obj -prune -exec rm -rf {} +
find "$P4/tests/phase4" -type d -name bin -prune -exec rm -rf {} +
rm -rf "$P4/src/korlib/obj" "$P4/src/korlib/bin"

cd "$P4"
for app in $APPS; do
  echo "=== $app ==="
  dotnet build "tests/phase4/$app.csproj" -c Release -o "$OUT" --nologo -v q 2>&1 | grep -E 'error|Build succeeded' | head -10
done
echo "=== outputs ==="
ls "$OUT" | grep -E 'phase4|p4net|p10hello|MathShared'
