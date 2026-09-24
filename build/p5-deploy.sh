#!/bin/bash
# Phase 5 deploy: fresh run image from the current build + the utility
# suite in ::/bin + an /etc/profile sample. Prints the resulting layout.
set -euo pipefail
cd /root/neutrino

# Never wait for input: mtools prompts and similar would deadlock inside
# the PowerShell -> WSL script chain (no interactive tty).
exec < /dev/null
export MTOOLS_SKIP_CHECK=1

cp -f build/x64/neutrinoos.img /root/run.img

# /bin with the utilities
mmd -i /root/run.img ::/bin 2>/dev/null || true
for f in /root/phase5bin/*.dll; do
  [ -f "$f" ] || continue
  mcopy -i /root/run.img -o "$f" "::/bin/$(basename "$f")"
done

# Keep the root copy of the DDK in sync with the freshly built one:
# utilities resolve their ProtonOS.DDK reference to the root copy (the
# same instance the kernel driver world uses), so a stale root DLL
# would silently run old DDK code in every utility.
mcopy -i /root/run.img -o /root/phase5bin/ProtonOS.DDK.dll ::/ProtonOS.DDK.dll

# /etc with a default profile (sourced by the shell at startup)
mmd -i /root/run.img ::/etc 2>/dev/null || true
cat > /root/profile.sample <<'EOF'
# /etc/profile - NeutrinoOS shell startup (Phase 5)
export PATH=/bin:/apps
export TERM=vt100
EOF
mcopy -i /root/run.img -o /root/profile.sample ::/etc/profile

echo "=== /bin ==="
mdir -i /root/run.img ::/bin
echo "=== /etc ==="
mdir -i /root/run.img ::/etc
echo "=== root ==="
mdir -i /root/run.img ::/ | head -25
