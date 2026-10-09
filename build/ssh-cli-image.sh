#!/bin/bash
# Prepare the CLI image for SSH use (docs/SSH.md):
#   - apply the user fixture (user account / password "neutrino",
#     /home/user + .ssh, /etc/ssh/sshd_config with PasswordAuthentication=yes)
#     via the Phase 6 image-extras script
#   - optionally install an authorized_keys (arg 1: path to a *.pub file,
#     e.g. /mnt/c/Users/<you>/.ssh/id_ed25519.pub)
#
# The fixture is applied IN PLACE to build/x64/neutrinoos-cli.img and the
# Windows copy build/neutrinoos-cli.img. A rebuild regenerates a pristine
# image - run this script again afterwards.
set -eu
cd /root/neutrino
IMG=build/x64/neutrinoos-cli.img
[ -f "$IMG" ] || { echo "missing $IMG - run build/rebuild-cli-image.sh first"; exit 1; }

echo "== applying user fixture (user/neutrino, sshd_config)..."
bash /mnt/d/Projects/Code/NeutrinoOS/build/p6-image-extras.sh "$IMG"

if [ "$#" -ge 1 ]; then
  PUB="$1"
  [ -f "$PUB" ] || { echo "pubkey not found: $PUB"; exit 1; }
  export MTOOLS_SKIP_CHECK=1
  mcopy -o -i "$IMG" "$PUB" ::/home/user/.ssh/authorized_keys
  echo "== authorized_keys installed from $PUB"
fi

cp -f "$IMG" /mnt/d/Projects/Code/NeutrinoOS/build/neutrinoos-cli.img
echo "=== SSH-ready CLI image:"
md5sum "$IMG" /mnt/d/Projects/Code/NeutrinoOS/build/neutrinoos-cli.img
echo "=== /etc/ssh:"
mdir -i "$IMG" ::/etc/ssh || true
echo "=== /home/user/.ssh:"
mdir -i "$IMG" ::/home/user/.ssh || true
