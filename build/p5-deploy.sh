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

# /bin with the utilities.
# NOTE: mtools' mmd loops forever on its "directory exists" overwrite
# prompt when stdin is closed (observed as a hung deploy) - pre-check with
# mdir, which never prompts.
mdir -i /root/run.img ::/bin >/dev/null 2>&1 || mmd -i /root/run.img ::/bin
for f in /root/phase5bin/*.dll; do
  [ -f "$f" ] || continue
  mcopy -i /root/run.img -o "$f" "::/bin/$(basename "$f")"
done

# Keep the root copy of the DDK in sync with the freshly built one:
# utilities resolve their NeutrinoOS.DDK reference to the root copy (the
# same instance the kernel driver world uses), so a stale root DLL
# would silently run old DDK code in every utility.
mcopy -i /root/run.img -o /root/phase5bin/NeutrinoOS.DDK.dll ::/NeutrinoOS.DDK.dll

# M4: hosted web apps -> /apps/webapp (launched by `webhost <app.dll>`).
# M8: app source directories define what IS an app; every other dll in
# appsbin (NuGet/third-party dependencies, the DDK copy) ships to ::/lib,
# where the loader resolves app dependencies by assembly name
# (AssemblyLoader.TryLoadAssemblyFromLib -> "/lib/<name>.dll").
# The base image already ships ::/apps (sample apps), so it must be
# pre-checked (see the mmd note above).
if [ -d /root/appsbin ]; then
  mdir -i /root/run.img ::/apps >/dev/null 2>&1 || mmd -i /root/run.img ::/apps
  mdir -i /root/run.img ::/apps/webapp >/dev/null 2>&1 || mmd -i /root/run.img ::/apps/webapp
  mdir -i /root/run.img ::/lib >/dev/null 2>&1 || mmd -i /root/run.img ::/lib
  for f in /root/appsbin/*.dll; do
    [ -f "$f" ] || continue
    base="$(basename "$f" .dll)"
    if [ -d "src/apps/$base" ]; then
      mcopy -i /root/run.img -o "$f" "::/apps/webapp/$(basename "$f")"
    else
      mcopy -i /root/run.img -o "$f" "::/lib/$(basename "$f")"
    fi
  done
fi

# /etc with a default profile (sourced by the shell at startup)
mdir -i /root/run.img ::/etc >/dev/null 2>&1 || mmd -i /root/run.img ::/etc
cat > /root/profile.sample <<'EOF'
# /etc/profile - NeutrinoOS shell startup (Phase 5)
export PATH=/bin:/apps
export TERM=vt100
EOF
mcopy -i /root/run.img -o /root/profile.sample ::/etc/profile

echo "=== /bin ==="
mdir -i /root/run.img ::/bin
echo "=== /apps/webapp ==="
mdir -i /root/run.img ::/apps/webapp 2>/dev/null || true
echo "=== /lib ==="
mdir -i /root/run.img ::/lib 2>/dev/null || true
echo "=== /etc ==="
mdir -i /root/run.img ::/etc
echo "=== root ==="
mdir -i /root/run.img ::/ | head -25
