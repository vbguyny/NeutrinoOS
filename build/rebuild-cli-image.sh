#!/bin/bash
# Full CLI image rebuild: kernel -> utilities -> deploy image -> CLI image.
# Mirrors the known-good chain used by scripts/cli-vm.ps1 -Rebuild.
set -euo pipefail
cd /root/neutrino
B=/mnt/d/Projects/Code/NeutrinoOS/build
export MTOOLS_SKIP_CHECK=1

echo "=== [1/4] kernel rebuild ==="
bash "$B/wsl-rebuild.sh" 2>&1 | tail -4

echo "=== [2/4] utility suite build ==="
bash "$B/p5-apps-build.sh" 2>&1 | tail -8

echo "=== [3/4] deploy image (kernel + /bin utilities + /etc) ==="
bash "$B/p5-deploy.sh" 2>&1 | tail -10

echo "=== [4/4] CLI image -> Windows build/neutrinoos-cli.img ==="
bash "$B/cli-image.sh" 2>&1 | tail -30

echo "=== REBUILD COMPLETE ==="
ls -la /root/run.img build/x64/neutrinoos-cli.img
md5sum build/x64/neutrinoos-cli.img /mnt/d/Projects/Code/NeutrinoOS/build/neutrinoos-cli.img
