#!/bin/bash
# Build the Phase 5 VirtualBox CLI image:
#   - the CLI variant (VGA console mirrored, console-active-vga +
#     skip-boot-tests markers) produced by build/cli-image.sh, plus
#   - the Phase 5 utility suite (32 x .NET 10) in ::/bin,
#   - /etc/profile and the synced root NeutrinoOS.DDK.dll copy (the same
#     instance the driver world and the utilities share).
#
# Usage (after build/p5-all.sh has built the kernel + utilities):
#   bash /mnt/d/Projects/Code/NeutrinoOS/build/p5-vbox-image.sh
set -eu
cd /root/neutrino

bash /mnt/d/Projects/Code/NeutrinoOS/build/cli-image.sh

DST=build/x64/neutrinoos-cli.img

# --- Phase 5 utilities --------------------------------------------------------
mmd -i "$DST" ::/bin 2>/dev/null || true
if [ -d /root/phase5bin ]; then
  for f in /root/phase5bin/*.dll; do
    [ -f "$f" ] || continue
    mcopy -o -i "$DST" "$f" "::/bin/$(basename "$f")"
  done
else
  echo "ERROR: /root/phase5bin missing - run build/p5-all.sh first"
  exit 1
fi

# Keep the root DDK copy in sync (utilities resolve their reference to
# the root copy - the same instance the kernel/driver world uses).
if [ -f /root/phase5bin/NeutrinoOS.DDK.dll ]; then
  mcopy -o -i "$DST" /root/phase5bin/NeutrinoOS.DDK.dll ::/NeutrinoOS.DDK.dll
fi

# --- /etc/profile -------------------------------------------------------------
mmd -i "$DST" ::/etc 2>/dev/null || true
cat > /root/profile.sample <<'EOF'
# /etc/profile - NeutrinoOS shell startup (Phase 5)
export PATH=/bin:/apps
export TERM=vt100
EOF
mcopy -o -i "$DST" /root/profile.sample ::/etc/profile

# Re-copy to the Windows side: cli-image.sh copied before our additions.
cp -f "$DST" /mnt/d/Projects/Code/NeutrinoOS/build/neutrinoos-cli.img

echo "=== Phase 5 VBox image ready ==="
mdir -i "$DST" ::/bin | tail -5
mdir -i "$DST" :: | grep -iE 'skip|console|NEUTRINO' || true
echo "=== copied to /mnt/d/Projects/Code/NeutrinoOS/build/neutrinoos-cli.img"
