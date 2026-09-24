#!/bin/bash
# Phase 7: assemble the v1.0.0 release artifacts in /root/neutrino/dist.
# Inputs:  build/x64/neutrinoos-gui.img  (run build/p7-release-image.sh first)
# Outputs: neutrinoos-1.0.0.img      raw VM image (20 GB boot-from-file install medium)
#          neutrinoos-1.0.0.qcow2    KVM/QEMU image
#          SHA256SUMS               checksums of all artifacts
#          SHA256SUMS.asc           detached ASCII-armor signature (if a key exists)
#          release.json             machine-readable release manifest
set -eu
cd /root/neutrino

VERSION="1.0.0"
SRC=build/x64/neutrinoos-gui.img
DIST=dist

[ -f "$SRC" ] || { echo "missing $SRC - run build/p7-release-image.sh first"; exit 1; }

rm -rf $DIST
mkdir -p $DIST

echo "[release] copying raw image..."
cp "$SRC" $DIST/neutrinoos-$VERSION.img

echo "[release] converting qcow2..."
qemu-img convert -f raw -O qcow2 $DIST/neutrinoos-$VERSION.img $DIST/neutrinoos-$VERSION.qcow2

echo "[release] checksums..."
( cd $DIST && sha256sum neutrinoos-$VERSION.img neutrinoos-$VERSION.qcow2 > SHA256SUMS )

# --- machine-readable manifest -------------------------------------------------
python3 - "$VERSION" <<'PYEOF'
import hashlib, json, os, sys
version = sys.argv[1]
dist = "dist"
files = []
for name in [f"neutrinoos-{version}.img", f"neutrinoos-{version}.qcow2"]:
    path = os.path.join(dist, name)
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    files.append({"name": name, "sha256": h.hexdigest(), "size": os.path.getsize(path)})
manifest = {
    "name": "neutrinoos",
    "version": version,
    "released": "2026-09-24",
    "artifacts": files,
    "install": {
        "windows_hyperv": "flash raw .img to a VHDX or use the raw image directly as a virtual disk",
        "windows_virtualbox": "see scripts/install-neutrinoos.ps1 (OVA import) or attach the raw .img",
        "qemu": f"qemu-system-x86_64 -m 2G -drive if=pflash,format=raw,readonly=on,file=OVMF_CODE.fd -drive format=qcow2,file=neutrinoos-{version}.qcow2 -serial stdio",
        "usb": "scripts/flash-usb.ps1 -Image neutrinoos-1.0.0.img -Drive E:"
    },
}
with open(os.path.join(dist, "release.json"), "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
print("[release] release.json written")
PYEOF

# --- signature -----------------------------------------------------------------
if gpg --list-secret-keys 2>/dev/null | grep -q '^sec'; then
  echo "[release] signing SHA256SUMS..."
  ( cd $DIST && gpg --detach-sign --armor --yes SHA256SUMS )
else
  echo "[release] WARNING: no GPG secret key in this environment - SHA256SUMS is UNSIGNED."
  echo "[release]   Generate the project key with:"
  echo "[release]     gpg --batch --generate-key <<< '%no-protection'"
  echo "[release]     gpg --quick-gen-key 'NeutrinoOS Release <release@neutrinoos.invalid>' ed25519 sign never"
  echo "[release]   then publish: gpg --armor --export release@neutrinoos.invalid > dist/RELEASE-KEY.asc"
fi

echo "=== dist contents:"
ls -la $DIST
