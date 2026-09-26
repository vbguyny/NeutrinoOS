#!/bin/bash
# Task 4 templates: lay out SDK props/targets in templates, rename files,
# clean build junk. Re-runnable.
set -u
cd /mnt/d/Projects/Code/NeutrinoOS

for t in NeutrinoConsoleApp NeutrinoUtility NeutrinoLibrary NeutrinoWebApp; do
  cp -f sdk/build/NeutrinoOS.App.props  "templates/$t/neutrino-app.props"
  cp -f sdk/build/NeutrinoOS.App.targets "templates/$t/neutrino-app.targets"
done

[ -f templates/NeutrinoDriver/NeutrinoDriver.csproj ] && mv -f templates/NeutrinoDriver/NeutrinoDriver.csproj templates/NeutrinoDriver/MyDriver.csproj
[ -f templates/NeutrinoWebApp/webapp.csproj ] && mv -f templates/NeutrinoWebApp/webapp.csproj templates/NeutrinoWebApp/NeutrinoWebApp.csproj

rm -rf templates/*/bin templates/*/obj

echo "=== templates tree ==="
find templates -type f | sort
