#!/bin/bash
# Phase 7 helper: sync the fixed release-image script into the build tree,
# kill any hung release chain, and report.
set -u
cp -f /mnt/d/Projects/Code/NeutrinoOS/build/p7-release-image.sh /root/neutrino/build/p7-release-image.sh
sed -i 's/\r$//' /root/neutrino/build/p7-release-image.sh
echo "mk_dir guards present: $(grep -c mk_dir /root/neutrino/build/p7-release-image.sh)"

MP=$(ps -o pid= -C make | head -1)
if [ -n "$MP" ]; then
  kill -9 "$MP" 2>/dev/null || true
  echo "killed make pid $MP"
fi

# bracket trick keeps this pattern from matching our own command line
pkill -9 -f 'p7-release-imag[e]' 2>/dev/null || true
pkill -9 -f 'make releas[e]' 2>/dev/null || true
sleep 1
echo RELFIX_OK
