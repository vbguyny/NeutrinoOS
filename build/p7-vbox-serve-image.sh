#!/bin/bash
# Phase 7 VBox acceptance fixture: copy the release image and bake
# /etc/boot.params so dhcp + sshd + webhost autostart on boot. This lets
# the Windows-side script verify services with file-serial only (no
# interactive console session needed).
set -eu
cd /root/neutrino
SRC=build/x64/neutrinoos-gui.img
DST=/mnt/d/Projects/Code/NeutrinoOS/build/vbox-p7-serve.img

[ -f "$SRC" ] || { echo "missing $SRC"; exit 1; }
cp -f "$SRC" "$DST"

cat > /root/p7-vbox-boot.params <<'EOF'
# Phase 7 VBox acceptance fixture (test image only; the shipped release
# keeps services opt-in - see docs/PHASE7-SECURITY.md secure defaults).
net.ip=dhcp
sshd.autostart=yes
webhost.autostart=yes
EOF

# /etc exists in the release image; guard anyway (mtools mmd prompts on
# existing directories).
if ! mdir -i "$DST" ::/etc > /dev/null 2>&1; then
  timeout -s KILL 10 mmd -i "$DST" ::/etc > /dev/null 2>&1 || true
fi
mcopy -o -i "$DST" /root/p7-vbox-boot.params ::/etc/boot.params

echo "== serve image ready: $DST"
mdir -i "$DST" ::/etc
