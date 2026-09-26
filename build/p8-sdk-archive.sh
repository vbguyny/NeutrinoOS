#!/bin/bash
# Phase 8 Task 4: assemble the downloadable NeutrinoOS SDK archive.
#
#   bash build/p8-sdk-archive.sh            -> dist/neutrinoos-sdk-<ver>.tar.gz
#   P8_SDK_VERSION=1.1.0 bash ...           -> custom version
#
# Contents: bin/npkg-host (+ npkg-repo-server), lib/ (Runtime/DDK,
# Driver.Abstractions, Packaging), build/ (MSBuild props+targets),
# templates/ (dotnet new), samples/, ci/, docs/ (SDK guides), LICENSE.
set -euo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
VERSION=${P8_SDK_VERSION:-1.0.0}
STAGE=/root/p8sdk-stage/neutrinoos-sdk
LIBS=/root/p8sdk-libs
OUT=$SRC/dist/neutrinoos-sdk-$VERSION.tar.gz

rm -rf /root/p8sdk-stage "$LIBS"
mkdir -p "$STAGE"/bin "$STAGE"/lib "$STAGE"/build "$STAGE"/templates "$STAGE"/samples "$STAGE"/ci "$STAGE"/docs
mkdir -p "$LIBS"

echo "--- 1/6 host tools"
bash "$SRC/build/p8-sdk-build.sh" > /tmp/p8-sdk-tool.log 2>&1
cp -a /root/p8sdk/. "$STAGE/bin/"

dotnet build "$SRC/sdk/repo-server/Npkg.RepoServer.csproj" -c Release -o /tmp/p8-sdk-repo > /tmp/p8-sdk-repo.log 2>&1
cp /tmp/p8-sdk-repo/npkg-repo-server* "$STAGE/bin/"

echo "--- 2/6 libraries"
dotnet build "$SRC/src/lib/NeutrinoOS.Packaging/NeutrinoOS.Packaging.csproj" -c Release -o "$LIBS" > /tmp/p8-sdk-lib1.log 2>&1
dotnet build "$SRC/src/lib/NeutrinoOS.Driver.Abstractions/NeutrinoOS.Driver.Abstractions.csproj" -c Release -o "$LIBS" > /tmp/p8-sdk-lib2.log 2>&1
dotnet build "$SRC/src/ddk/DDK.csproj" -c Release -o "$LIBS" > /tmp/p8-sdk-lib3.log 2>&1
if dotnet build "$SRC/src/korlib/korlib.csproj" -c Release -o "$LIBS" > /tmp/p8-sdk-lib4.log 2>&1; then
  echo "korlib.dll built"
else
  echo "WARN: korlib.csproj did not build standalone; omitting korlib.dll"
fi
for dll in NeutrinoOS.Packaging.dll NeutrinoOS.Driver.Abstractions.dll ProtonOS.DDK.dll korlib.dll; do
  [ -f "$LIBS/$dll" ] && cp "$LIBS/$dll" "$STAGE/lib/"
done
cp "$LIBS"/*.xml "$STAGE/lib/" 2>/dev/null || true

echo "--- 3/6 msbuild integration"
cp "$SRC/sdk/build/NeutrinoOS.App.props" "$SRC/sdk/build/NeutrinoOS.App.targets" "$STAGE/build/"

echo "--- 4/6 templates + samples + ci"
cp -a "$SRC/templates/." "$STAGE/templates/"
rm -rf "$STAGE"/templates/*/bin "$STAGE"/templates/*/obj
cp -a "$SRC/samples/." "$STAGE/samples/"
rm -rf "$STAGE"/samples/*/bin "$STAGE"/samples/*/obj
cp -a "$SRC/sdk/ci/." "$STAGE/ci/"

echo "--- 5/6 docs + license"
for doc in SDK-GETTING-STARTED.md SDK-PACKAGING.md SDK-TESTING.md SDK-CICD.md SDK-API-REFERENCE.md; do
  [ -f "$SRC/docs/$doc" ] && cp "$SRC/docs/$doc" "$STAGE/docs/"
done
cp "$SRC/LICENSE" "$STAGE/" 2>/dev/null || true

cat > "$STAGE/README.md" <<EOF
# NeutrinoOS SDK $VERSION

Build applications, utilities, drivers and libraries for NeutrinoOS from
Windows 11 (or any .NET 10 host).

  bin/npkg-host           package tool (pack/sign/verify/list/repo-index/publish)
  bin/npkg-repo-server    local package repository over HTTP
  lib/                    the guest API surfaces (DDK, Driver.Abstractions,
                          Packaging)
  build/                  MSBuild props/targets (auto .npkg on Release builds)
  templates/              dotnet new templates (console/utility/driver/webapp/library)
  samples/                five buildable sample projects
  ci/                     GitHub Actions + GitLab CI workflow templates
  docs/                   getting started, packaging, testing, CI/CD, API reference

Quick start:

    dotnet new install ./templates
    dotnet new neutrino-console -n MyApp
    cd MyApp
    dotnet build -c Release          # -> bin/Release/net10.0/MyApp.npkg

See docs/SDK-GETTING-STARTED.md.
EOF

echo "--- 6/6 archive"
mkdir -p "$SRC/dist"
tar -C /root/p8sdk-stage -czf "$OUT" neutrinoos-sdk
echo "=== SDK ARCHIVE OK ==="
ls -la "$OUT"
echo "--- contents (top) ---"
tar -tzf "$OUT" | head -30 || true
echo "--- lib/ ---"
tar -tzf "$OUT" | grep 'lib/' | head -10 || true
