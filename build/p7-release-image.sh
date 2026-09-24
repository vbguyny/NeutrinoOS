#!/bin/bash
# Phase 7: build the release image variant.
#   - GUI variant of the kernel (VGA mirrored, skip-boot-tests marker)
#   - the 36-utility suite in ::/bin plus /etc/profile
#   - /etc/neutrinoos-release version file
# Result: /root/neutrino/build/x64/neutrinoos-gui.img (release candidate),
# which build/p7-release.sh turns into the distribution artifacts.
set -eu
cd /root/neutrino

VERSION="1.0.0"

# GUI image + utilities + /etc/profile (Phase 5 chain).
bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-vbox-image.sh

DST=build/x64/neutrinoos-gui.img

# --- /etc/neutrinoos-release --------------------------------------------------
cat > /root/neutrinoos-release <<EOF
NAME="NeutrinoOS"
VERSION="$VERSION"
ID=neutrinoos
PRETTY_NAME="NeutrinoOS $VERSION (Phase 7)"
HOME_URL="https://github.com/vbguyny/NeutrinoOS"
EOF

# mtools prompts (on /dev/tty!) when asked to create an existing
# directory, which deadlocks a non-interactive run. Guard with mdir.
mk_dir() {
  if ! timeout -s KILL 10 mdir -i "$DST" "::$1" > /dev/null 2>&1; then
    timeout -s KILL 10 mmd -i "$DST" "::$1" > /dev/null 2>&1 || true
  fi
}

mk_dir /etc
mcopy -o -i "$DST" /root/neutrinoos-release ::/etc/neutrinoos-release

# Standard writable areas for services.
mk_dir /var
mk_dir /var/log
mk_dir /var/www
mk_dir /home

# p5-vbox-image.sh synced the image to the Windows tree before the release
# file was added above; refresh the synced copy so both are identical.
cp -f "$DST" /mnt/d/Projects/Code/NeutrinoOS/build/neutrinoos-gui.img

# Belt and braces: never let mtools read from the terminal.
exec < /dev/null

echo "=== release image ready: $DST"
mdir -i "$DST" ::/etc
mdir -i "$DST" ::/bin | tail -3
