#!/bin/bash
# Build a "GUI" image variant for manual QEMU/VirtualBox boots:
#   - VGA text console enabled and mirrored from early boot (visible in
#     the VM window), no console-vga-off marker
#   - console-active-vga: the VGA console (PS/2 keyboard) is the initial
#     active input, so typing in the VM window works; serial input
#     auto-switches back when serial bytes arrive
#   - skip-boot-tests: fast manual boots (drop this marker in the copy
#     if you want the full test suites to run)
#
# The plain build/x64/neutrinoos.img (used by the automated test scripts,
# which add their own markers) is left untouched.
#
# Usage: bash build/gui-image.sh
set -eu
cd /root/neutrino

SRC=build/x64/neutrinoos.img
DST=build/x64/neutrinoos-gui.img

[ -f "$SRC" ] || { echo "ERROR: $SRC not found - build first"; exit 1; }

cp -f "$SRC" "$DST"

echo 1 > /tmp/skip-boot-tests
echo 1 > /tmp/console-active-vga
# Removal is idempotent: markers may or may not be present depending on
# which test script ran last (mdel exits nonzero if a file is missing).
mdel -i "$DST" ::/console-vga-off ::/skip-boot-tests ::/console-active-vga || true
mcopy -o -i "$DST" /tmp/skip-boot-tests ::/skip-boot-tests
mcopy -o -i "$DST" /tmp/console-active-vga ::/console-active-vga

# --- Copy the Phase 4 test apps so the VM can run them -----------------------
# Built by build/p4-apps-build.sh into /root/phase4apps. Missing apps only
# warn: the image still boots for manual boot-log inspection.
if [ -d /root/phase4apps ]; then
  for f in p4hello p4fileio p4linq p4async p4cs14 p4inter p4multi p4net; do
    if [ -f "/root/phase4apps/$f.dll" ]; then
      mcopy -o -i "$DST" "/root/phase4apps/$f.dll" "::/apps/$f.dll"
    else
      echo "WARN: /root/phase4apps/$f.dll missing - run build/p4-apps-build.sh"
    fi
  done
  if [ -f /root/phase4apps/p4math.dll ]; then
    mcopy -o -i "$DST" /root/phase4apps/p4math.dll ::/lib/p4math.dll
  fi
else
  echo "WARN: /root/phase4apps not found - GUI image will not contain the p4 apps"
fi

cp -f "$DST" /mnt/d/Projects/Code/NeutrinoOS/build/neutrinoos-gui.img

echo "=== markers in GUI image:"
mdir -i "$DST" :: | grep -i 'skip\|console' || true
echo "=== apps in GUI image:"
mdir -i "$DST" ::/apps | tail -14
echo "=== copied to /mnt/d/Projects/Code/NeutrinoOS/build/neutrinoos-gui.img"
