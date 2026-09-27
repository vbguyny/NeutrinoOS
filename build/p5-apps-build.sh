#!/bin/bash
# Phase 5 utility suite build: sync src/utilities to WSL, generate the
# per-utility .NET 10 project files (the repo keeps only Program.cs +
# the shared Common helper; the projects are generated here), and build
# every utility into /root/phase5bin (plus an aggregate report).
set -euo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Optional: TRACE=1 in the environment (or --trace as the first argument)
# compiles the DDK/utility trace output in (the DDK Debug class is
# [Conditional("NEUTRINO_TRACE")]; default builds drop those call sites).
if [ "${1:-}" = "--trace" ]; then
  export TRACE=1
fi
TRACEARG=""
if [ "${TRACE:-}" = "1" ]; then
  TRACEARG="-p:NeutrinoTrace=1"
fi

SRC=/mnt/d/Projects/Code/NeutrinoOS
WORK=/root/phase5-utils
OUT=/root/phase5bin

rsync -a --delete --exclude obj --exclude bin "$SRC/src/utilities/" "$WORK/"
rsync -a --delete --exclude obj --exclude bin "$SRC/src/ddk/" "$WORK/ddk/"
# Phase 8: shared NeutrinoOS.Packaging sources (compiled into the npkg utility).
if [ -d "$SRC/src/lib/NeutrinoOS.Packaging" ]; then
  mkdir -p "$WORK/lib"
  rsync -a --delete --exclude obj --exclude bin "$SRC/src/lib/NeutrinoOS.Packaging/" "$WORK/lib/NeutrinoOS.Packaging/"
fi
rm -rf "$OUT"
mkdir -p "$OUT"

cd "$WORK"

fail=0
count=0
for dir in */; do
  name="${dir%/}"
  # Common is the shared source directory; ddk is the reference project.
  [ "$name" = "Common" ] && continue
  [ "$name" = "ddk" ] && continue
  [ "$name" = "lib" ] && continue
  [ -f "$name/Program.cs" ] || continue

  # Phase 8: the npkg utility compiles the shared packaging sources in.
  extra=""
  if [ "$name" = "npkg" ]; then
    extra='    <Compile Include="../lib/NeutrinoOS.Packaging/*.cs" />'
  fi

  cat > "$name/$name.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>$name</AssemblyName>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <NoWarn>\$(NoWarn);CS0436</NoWarn>
  </PropertyGroup>
  <PropertyGroup Condition="'\$(NeutrinoTrace)' == '1'">
    <DefineConstants>\$(DefineConstants);NEUTRINO_TRACE</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../Common/UtilCommon.cs" />
    <Compile Include="../Common/HttpCommon.cs" />
$extra
    <ProjectReference Include="../ddk/DDK.csproj" />
  </ItemGroup>
</Project>
EOF

  if dotnet build "$name/$name.csproj" -c Release -o "$OUT" --nologo -v q $TRACEARG; then
    count=$((count+1))
  else
    echo "=== BUILD FAILED: $name ==="
    fail=1
  fi
done

echo "=== $count utilities built ==="
ls -1 "$OUT" | grep '\.dll$' | sort
exit $fail
