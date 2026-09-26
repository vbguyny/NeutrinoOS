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
# Benchmark fixtures (spec: "npkg install time for a package with 10
# dependencies") are generated: tests.bench-root depends on ten leaf
# packages tests.bench-dep-1..10. The install benchmark is
# build/p8-t6-bench.sh.
python3 - "$WORK/bench" <<'PY'
import json, os, sys
base = sys.argv[1]
for i in range(1, 11):
    d = os.path.join(base, "dep-%d" % i)
    os.makedirs(d, exist_ok=True)
    man = {
        "name": "tests.bench-dep-%d" % i,
        "version": "1.0.0",
        "architecture": "any",
        "provides": ["utility"],
        "dependencies": {},
        "entryPoints": {"benchdep%d" % i: "benchdep%d.dll" % i},
        "installPath": "/bin",
        "signer": "",
    }
    with open(os.path.join(d, "manifest.json"), "w") as f:
        json.dump(man, f, indent=2)
    with open(os.path.join(d, "Program.cs"), "w") as f:
        f.write('public static class Program\n{\n'
                '    public static int Main(string[] args)\n    {\n'
                '        System.Console.WriteLine("bench-dep-%d ok");\n'
                '        return 0;\n    }\n}\n' % i)
d = os.path.join(base, "root")
os.makedirs(d, exist_ok=True)
man = {
    "name": "tests.bench-root",
    "version": "1.0.0",
    "architecture": "any",
    "provides": ["utility"],
    "dependencies": {("tests.bench-dep-%d" % i): ">=1.0.0" for i in range(1, 11)},
    "entryPoints": {"benchroot": "benchroot.dll"},
    "installPath": "/bin",
    "signer": "",
}
with open(os.path.join(d, "manifest.json"), "w") as f:
    json.dump(man, f, indent=2)
with open(os.path.join(d, "Program.cs"), "w") as f:
    f.write('public static class Program\n{\n'
            '    public static int Main(string[] args)\n    {\n'
            '        System.Console.WriteLine("bench-root ok");\n'
            '        return 0;\n    }\n}\n')
print("benchmark fixtures generated under", base)
PY

fixtures=(
  hello-utility
  hello-utility-1.1
  hello-app
  hello-driver
  dependency-chain/chain-a
  dependency-chain/chain-b
  dependency-chain/chain-c
  conflict/libz-1
  conflict/libz-2
  conflict/conflict-w
  conflict/conflict-x
  conflict/conflict-y
  bench/dep-1
  bench/dep-2
  bench/dep-3
  bench/dep-4
  bench/dep-5
  bench/dep-6
  bench/dep-7
  bench/dep-8
  bench/dep-9
  bench/dep-10
  bench/root
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
  # Build without -o: `dotnet build -o` would also dump the referenced
  # projects' outputs (the driver ABI assembly + friends) into the payload
  # dir; Private=false only suppresses copy-local, not the -o redirection.
  # Stage exactly the fixture's own assembly instead.
  dotnet build "$fdir/P8Fixture.csproj" -c Release --nologo -v q
  cp "$fdir/bin/Release/net10.0/$asm.dll" "$payload/"

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

  # ---- (e) tampered package (checksum-rejection fixture) ---------------
  # Rebuild a copy of hello-utility 1.0.0 with one byte flipped inside the
  # stored payload dll. The container stays fully valid (python recomputes
  # CRCs; manifest.json and checksums.sha256 are byte-identical), so the
  # verifier reads it cleanly and fails on the payload checksum
  # ("checksums: FAIL"). A naive in-place byte flip corrupted the archive
  # structure and crashed the guest verifier - keep this rebuild method.
  python3 - "$REPO/tests.hello-utility-1.0.0.npkg" "$REPO/tampered.npkg" <<'PY'
import sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
zin = zipfile.ZipFile(src)
zout = zipfile.ZipFile(dst, 'w', zipfile.ZIP_STORED)
for info in zin.infolist():
    data = bytearray(zin.read(info.filename))
    if info.filename == 'payload/helloutil.dll':
        if len(data) < 128:
            raise SystemExit('payload too small to tamper')
        data[64] ^= 0xFF
    zout.writestr(info, bytes(data), compress_type=zipfile.ZIP_STORED)
zout.close()
print('tampered package written:', dst)
PY

  # Host-side sanity: the doubled package must still parse (list shows the
  # real manifest) and fail verification on the checksum.
  "$HOST" list "$REPO/tampered.npkg" > /tmp/p8-tamper-list.log 2>&1 \
    || { echo "error: tampered package does not parse:" >&2; cat /tmp/p8-tamper-list.log >&2; exit 1; }
  grep -qa "version:      1.0.0" /tmp/p8-tamper-list.log \
    || { echo "error: tampered package manifest damaged:" >&2; cat /tmp/p8-tamper-list.log >&2; exit 1; }
  if "$HOST" verify "$REPO/tampered.npkg" > /tmp/p8-tamper-check.log 2>&1; then
    echo "error: tampered package unexpectedly verified" >&2
    exit 1
  fi
  grep -qa "\[FAIL\] checksums" /tmp/p8-tamper-check.log \
    || { echo "error: tampered package did not report a checksum failure:" >&2; cat /tmp/p8-tamper-check.log >&2; exit 1; }
  echo "tamper fixture rejected by npkg-host verify (checksums FAIL)"
fi

echo "=== NPKG TEST PACKAGES OK ==="
ls -l /root/p8repo
