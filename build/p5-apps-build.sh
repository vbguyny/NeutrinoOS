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

# M4: hosted web apps (src/apps) -> /root/appsbin, deployed to /apps/webapp.
# Same project shape as the utilities; ProjectReference two levels up.
APPSRC="$SRC/src/apps"
APPWORK="$WORK/apps"
APPBIN=/root/appsbin
if [ -d "$APPSRC" ]; then
  rsync -a --delete --exclude obj --exclude bin "$APPSRC/" "$APPWORK/"
  rm -rf "$APPBIN"
  mkdir -p "$APPBIN"
  appcount=0
  for dir in "$APPWORK"/*/; do
    name="$(basename "$dir")"
    [ -f "$APPWORK/$name/Program.cs" ] || continue

    # Optional NuGet packages: src/apps/<name>/packages.txt, one
    # "PackageId [Version]" per line (# comments and blanks allowed).
    # The kernel resolves app dependencies from ::/lib by assembly name
    # (see p5-deploy.sh), so package dlls ship there.
    pkgrefs=""
    if [ -f "$APPWORK/$name/packages.txt" ]; then
      while IFS= read -r line; do
        line="$(printf '%s' "$line" | sed 's/#.*//' | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
        [ -n "$line" ] || continue
        pkgid="${line%% *}"
        pkgver=""
        if [ "$pkgid" != "$line" ]; then pkgver="${line#* }"; fi
        if [ -n "$pkgver" ]; then
          pkgrefs="${pkgrefs}    <PackageReference Include=\"${pkgid}\" Version=\"${pkgver}\" />
"
        else
          pkgrefs="${pkgrefs}    <PackageReference Include=\"${pkgid}\" />
"
        fi
      done < "$APPWORK/$name/packages.txt"
    fi
    pkgblock=""
    if [ -n "$pkgrefs" ]; then
      pkgblock="  <ItemGroup>
${pkgrefs}  </ItemGroup>"
    fi

    cat > "$APPWORK/$name/$name.csproj" <<EOF
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
    <ProjectReference Include="../../ddk/DDK.csproj" />
  </ItemGroup>
${pkgblock}</Project>
EOF

    if dotnet build "$APPWORK/$name/$name.csproj" -c Release -o "$APPBIN" --nologo -v q $TRACEARG; then
      appcount=$((appcount+1))
    else
      echo "=== BUILD FAILED (app): $name ==="
      fail=1
    fi
  done
  echo "=== $appcount web apps built ==="
  ls -1 "$APPBIN" 2>/dev/null | grep '\.dll$' | sort || true
fi

exit $fail
