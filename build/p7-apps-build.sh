#!/bin/bash
# Phase 7 benchmark build: compile tests/benchmarks/* into /root/phase7bin
# (standalone .NET 10 console apps referencing the DDK project).
set -euo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

SRC=/mnt/d/Projects/Code/NeutrinoOS
WORK=/root/phase7-bench
OUT=/root/phase7bin

rsync -a --delete --exclude obj --exclude bin "$SRC/tests/benchmarks/" "$WORK/"
rsync -a --delete --exclude obj --exclude bin "$SRC/src/ddk/" "$WORK/ddk/"
rm -rf "$OUT"
mkdir -p "$OUT"

cd "$WORK"

fail=0
count=0
for dir in */; do
  name="${dir%/}"
  [ "$name" = "ddk" ] && continue
  [ -f "$name/Program.cs" ] || continue

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
  <ItemGroup>
    <ProjectReference Include="../ddk/DDK.csproj" />
  </ItemGroup>
</Project>
EOF

  if dotnet build "$name/$name.csproj" -c Release -o "$OUT" --nologo -v q; then
    count=$((count+1))
  else
    echo "=== BUILD FAILED: $name ==="
    fail=1
  fi
done

echo "=== $count benchmarks built ==="
ls -1 "$OUT" | grep '\.dll$' | sort
exit $fail
