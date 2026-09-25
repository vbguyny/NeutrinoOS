#!/bin/bash
# Phase 8 npkg acceptance fixtures: build the host npkg CLI (if needed),
# build every test package payload with dotnet, pack the fixtures into
# /root/p8repo with npkg-host, and generate the signed repository index.
#
#   bash build/p8-npkg-tests-build.sh
#
# WSL-only, run as root.  Output: /root/p8repo/*.npkg together with
# repository.json, repository.json.sig and repo.pub.
#
# Debug aid: P8_PAYLOAD_ONLY=1 stops after building the fixture payloads
# (skips the SDK build, packing and the repository index).
set -euo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

PAYLOAD_ONLY=${P8_PAYLOAD_ONLY:-0}

SRC=/mnt/d/Projects/Code/NeutrinoOS
WORK=/root/p8pkg
REPO=/root/p8repo
HOST=/root/p8sdk/npkg-host
KEYS="$WORK/keys"

# ---- (a) host CLI: build when missing or when its sources are newer ----
stale=0
[ ! -x "$HOST" ] && stale=1
[ -f "$SRC/sdk/npkg/Program.cs" ] && [ "$SRC/sdk/npkg/Program.cs" -nt "$HOST" ] && stale=1
if [ -d "$SRC/src/lib/NeutrinoOS.Packaging" ] && \
   [ -n "$(find "$SRC/src/lib/NeutrinoOS.Packaging" "$SRC/src/ddk/Crypto" -name '*.cs' -newer "$HOST" -print -quit 2>/dev/null)" ]; then
  stale=1
fi
if [ "$PAYLOAD_ONLY" != 1 ] && [ "$stale" = 1 ]; then
  bash "$SRC/build/p8-sdk-build.sh"
fi

# ---- (b) WSL-side work tree (avoid drvfs build slowness) ---------------
rm -rf "$WORK"
mkdir -p "$WORK"
rsync -a --exclude obj --exclude bin "$SRC/tests/npkg/" "$WORK/"

if [ "$PAYLOAD_ONLY" != 1 ] && [ ! -f "$KEYS/private.key" ]; then
  echo "error: missing $KEYS/private.key - generate tests/npkg/keys first" >&2
  exit 1
fi

# ---- (c) fixtures: payload build + pack --------------------------------
fixtures=(
  hello-utility
  hello-app
  hello-driver
  dependency-chain/chain-a
  dependency-chain/chain-b
  dependency-chain/chain-c
  conflict/libz-1
  conflict/libz-2
  conflict/conflict-x
  conflict/conflict-y
)

rm -rf "$REPO"
mkdir -p "$REPO"

i=0
for fx in "${fixtures[@]}"; do
  i=$((i+1))
  fdir="$WORK/$fx"
  payload="$WORK/.payload/$i"

  # Package name/version and payload assembly name come from the manifest:
  # the assembly is the entry point file name without the .dll suffix.
  readarray -t meta < <(python3 - "$fdir/manifest.json" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
eps = m.get("entryPoints") or {}
if eps:
    ep = sorted(eps.values())[0]
elif m.get("driver"):
    ep = m["driver"]["entryPoint"]
else:
    ep = ""
asm = ep[:-4] if ep.endswith(".dll") else ep
if not asm:
    raise SystemExit("no entry point in " + sys.argv[1])
print(m["name"])
print(m["version"])
print(asm)
PY
  )
  name="${meta[0]}"; version="${meta[1]}"; asm="${meta[2]}"

  # Temp project next to the fixture sources (dll only, no apphost/pdb/deps).
  cat > "$fdir/P8Fixture.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>$asm</AssemblyName>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <NoWarn>\$(NoWarn);CS0436</NoWarn>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
    <UseAppHost>false</UseAppHost>
    <GenerateDependencyFile>false</GenerateDependencyFile>
    <GenerateRuntimeConfigurationFiles>false</GenerateRuntimeConfigurationFiles>
  </PropertyGroup>
  <ItemGroup>
    <!-- Fixtures compile against the real driver ABI. Private=false: the
         kernel provides the assembly at driver load time, it must never
         ship in a package payload. -->
    <ProjectReference Include="/root/neutrino/src/lib/NeutrinoOS.Driver.Abstractions/NeutrinoOS.Driver.Abstractions.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>
</Project>
EOF

  rm -rf "$payload"
  mkdir -p "$payload"
  dotnet build "$fdir/P8Fixture.csproj" -c Release -o "$payload" --nologo -v q

  if [ "$PAYLOAD_ONLY" != 1 ]; then
    "$HOST" pack --manifest "$fdir/manifest.json" --payload-dir "$payload" \
      --out "$REPO/$name-$version.npkg" --key "$KEYS/private.key"
  else
    echo "payload ok: $fx -> $asm.dll"
  fi
done

# ---- (d) signed repository index ---------------------------------------
if [ "$PAYLOAD_ONLY" != 1 ]; then
  "$HOST" repo-index --dir "$REPO" --key "$KEYS/private.key" --name local
fi

echo "=== NPKG TEST PACKAGES OK ==="
ls -l /root/p8repo
