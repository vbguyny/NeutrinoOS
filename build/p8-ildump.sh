#!/bin/bash
# Host-side IL body dumper (Phase 8 npkg crash triage).
# Usage: bash p8-ildump.sh <dll> <token> [token...]
set -uo pipefail
DLL="${1:?usage: p8-ildump.sh <dll> <token>...}"
shift || true
DIR=/root/ildump
if [ ! -f "$DIR/ildump.dll" ] && [ ! -f "$DIR/bin/ildump.dll" ]; then
  mkdir -p "$DIR"
  cat > "$DIR/ildump.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>
EOF
  cp /mnt/d/Projects/Code/NeutrinoOS/build/p8-ildump-src2.txt "$DIR/Program.cs"
  sed -i 's/\r$//' "$DIR/Program.cs"
  cd "$DIR" || exit 1
  dotnet build -c Release -o bin --nologo -v q > /root/ildump-build.log 2>&1 || { echo BUILD-FAILED; tail -25 /root/ildump-build.log; exit 1; }
fi
cd "$DIR" || exit 1
dotnet bin/ildump.dll "$DLL" "$@" 2>&1
