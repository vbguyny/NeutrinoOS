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

# WSL reports /mnt/d mtimes verbatim and the Windows clock can run ahead of the
# WSL clock, so freshly saved sources look newer than every build output and
# defeat the incremental check below. Stamp the time before syncing, then clamp
# anything newer than that stamp to now (only just-copied files can be).
sync_stamp=$(mktemp)
touch "$sync_stamp"
trap 'rm -f "$sync_stamp"' EXIT

rsync -a -c --delete --exclude obj --exclude bin --exclude '*/*.csproj' "$SRC/src/utilities/" "$WORK/"
rsync -a -c --delete --exclude obj --exclude bin "$SRC/src/ddk/" "$WORK/ddk/"
# Phase 8: shared NeutrinoOS.Packaging sources (compiled into the npkg utility).
if [ -d "$SRC/src/lib/NeutrinoOS.Packaging" ]; then
  mkdir -p "$WORK/lib"
  rsync -a --delete --exclude obj --exclude bin "$SRC/src/lib/NeutrinoOS.Packaging/" "$WORK/lib/NeutrinoOS.Packaging/"
fi
find "$WORK" -newer "$sync_stamp" \
     \( -name '*.cs' -o -name '*.csproj' -o -name '*.txt' \) \
     -exec touch {} + 2>/dev/null || true
if [ "${CLEAN:-0}" = "1" ]; then
  rm -rf "$OUT"
fi
mkdir -p "$OUT"

cd "$WORK"

# -- incremental --------------------------------------------------------------
# A project is rebuilt only when one of its inputs is newer than its output
# assembly: its own sources, the shared Common sources, the DDK project it
# references, and (for npkg) the packaged lib sources. Clean builds are still
# available with CLEAN=1.
needs_build() {  # $1 = project dir, $2 = output dll
  [ -f "$2" ] || return 0
  local newer
  # obj/bin hold generated *.cs (AssemblyInfo etc.) that are rewritten by every
  # build of the referenced project, so they must not count as inputs.
  newer=$(find "$1" "$WORK/Common" "$WORK/ddk" "$WORK/lib" \
                \( -name obj -o -name bin \) -prune -o \
                \( -name '*.cs' -o -name '*.txt' -o -name '*.csproj' \) \
                -newer "$2" -print -quit 2>/dev/null)
  [ -n "$newer" ]
}

# The .csproj files below are generated; rewriting an identical file would
# bump its mtime and defeat needs_build, so only write when the content changed.
write_if_changed() {  # $1 = path, generated content on stdin
  local tmp
  tmp=$(mktemp)
  cat > "$tmp"
  if ! cmp -s "$tmp" "$1"; then
    mv "$tmp" "$1"
  else
    rm -f "$tmp"
  fi
}

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

  write_if_changed "$name/$name.csproj" <<EOF
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

  if [ "${CLEAN:-0}" != "1" ] && ! needs_build "$name" "$OUT/$name.dll"; then
    count=$((count+1))
    continue
  fi

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
    rsync -a -c --delete --exclude obj --exclude bin --exclude '*/*.csproj' "$APPSRC/" "$APPWORK/"
  find "$APPWORK" -newer "$sync_stamp" \
       \( -name '*.cs' -o -name '*.csproj' -o -name '*.txt' \) \
       -exec touch {} + 2>/dev/null || true
  if [ "${CLEAN:-0}" = "1" ]; then
    rm -rf "$APPBIN"
  fi
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

    write_if_changed "$APPWORK/$name/$name.csproj" <<EOF
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

    if [ "${CLEAN:-0}" != "1" ] && ! needs_build "$APPWORK/$name" "$APPBIN/$name.dll"; then
      appcount=$((appcount+1))
      continue
    fi

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
