#!/bin/bash
# Phase 10 interoperability matrix (host side, WSL):
#
# For each volume variant (created by Linux mkfs.exfat):
#   1. mkfs.exfat with the variant options
#   2. linux-write: FUSE-mount (exfat-fuse via loop device), write fixture
#      files, unmount
#   3. neutrinos: harness lists and cats the linux files, then writes its
#      own files (create/rename/delete)
#   4. linux-verify: FUSE-mount again and verify the NeutrinoOS changes
#      are visible to Linux, fsck.exfat reports clean
#
# usage: p10-interop.sh [harness-dir]
set -u
HOST_DIR="${1:-/mnt/d/Projects/Code/NeutrinoOS/tests/exfat-host}"
WORK=/tmp/p10interop
MNT=/mnt/p10interop
H="$HOST_DIR/bin/Debug/net10.0/exfat-host"

mkdir -p "$WORK" "$MNT"
(cd "$HOST_DIR" && dotnet build -v q --nologo 2>&1 | grep -E "error" && exit 1) || true

pass=0
fail=0
check() {
    if grep -aq "$2" "$3" 2>/dev/null; then
        echo "PASS: $1"; pass=$((pass+1))
    else
        echo "FAIL: $1"; fail=$((fail+1))
    fi
}

LODEV=""
linux_mount() {
    LODEV=$(losetup -f --show "$1") || return 1
    mount.exfat-fuse "$LODEV" "$MNT" -o rw > /dev/null 2>&1 || { losetup -d "$LODEV"; LODEV=""; return 1; }
    return 0
}

linux_unmount() {
    sync
    fusermount -u "$MNT" 2>/dev/null || umount "$MNT"
    if [ -n "$LODEV" ]; then losetup -d "$LODEV" 2>/dev/null; fi
    LODEV=""
}

run_variant() {
    local name="$1"; shift
    local img="$WORK/$name.img"
    echo ""
    echo "===== variant: $name ($*) ====="
    rm -f "$img"
    truncate -s 64M "$img"

    mkfs.exfat "$@" "$img" > "$WORK/$name.mkfs" 2>&1 || { echo "FAIL: mkfs.exfat ($name)"; cat "$WORK/$name.mkfs"; fail=$((fail+1)); return; }
    echo "mkfs.exfat ok"

    if ! linux_mount "$img"; then
        echo "FAIL: linux FUSE mount ($name)"; fail=$((fail+1)); return
    fi
    echo "linux content" > "$MNT/linux.txt"
    mkdir -p "$MNT/linuxdir"
    printf 'nested-payload' > "$MNT/linuxdir/nested.bin"
    printf 'MixedCase contents' > "$MNT/MixedCase Name.txt"
    printf 'chunk-1-' > "$MNT/linuxdir/chain.dat"
    printf 'chunk-2-' >> "$MNT/linuxdir/chain.dat"
    printf 'chunk-3-' >> "$MNT/linuxdir/chain.dat"
    printf 'chunk-4-' >> "$MNT/linuxdir/chain.dat"
    linux_unmount

    "$H" "$img" list / > "$WORK/$name.list1" 2>&1
    check "$name: list shows linux.txt" "linux.txt" "$WORK/$name.list1"
    check "$name: list shows linuxdir" "linuxdir" "$WORK/$name.list1"
    check "$name: list shows MixedCase" "MixedCase Name.txt" "$WORK/$name.list1"

    "$H" "$img" cat /linux.txt > "$WORK/$name.cat" 2>&1
    check "$name: cat linux.txt content" "linux content" "$WORK/$name.cat"
    "$H" "$img" cat /linuxdir/nested.bin > "$WORK/$name.cat2" 2>&1
    check "$name: cat nested.bin content" "nested-payload" "$WORK/$name.cat2"
    "$H" "$img" cat /linuxdir/chain.dat > "$WORK/$name.cat3" 2>&1
    check "$name: cat chain.dat content" "chunk-4-" "$WORK/$name.cat3"

    "$H" "$img" stat /LINUX.TXT > "$WORK/$name.case" 2>&1
    check "$name: case-insensitive stat" "PASS: stat /LINUX.TXT" "$WORK/$name.case"

    "$H" "$img" write /neo.txt "hello from NeutrinoOS" > "$WORK/$name.w" 2>&1
    check "$name: write /neo.txt" "PASS: write /neo.txt" "$WORK/$name.w"
    "$H" "$img" mkdir /neodir > "$WORK/$name.d" 2>&1
    check "$name: mkdir /neodir" "PASS: mkdir /neodir" "$WORK/$name.d"
    "$H" "$img" write /linuxdir/fromneo.dat "neo-into-linux-dir" > "$WORK/$name.w2" 2>&1
    check "$name: write into linux dir" "PASS: write /linuxdir/fromneo.dat" "$WORK/$name.w2"
    "$H" "$img" mv /neo.txt /renamed.txt > "$WORK/$name.mv" 2>&1
    check "$name: rename /neo.txt" "PASS: renamed exists" "$WORK/$name.mv"
    "$H" "$img" rm /linuxdir/nested.bin > "$WORK/$name.rm" 2>&1
    check "$name: delete linux file" "PASS: rm /linuxdir/nested.bin" "$WORK/$name.rm"

    if ! linux_mount "$img"; then
        echo "FAIL: linux FUSE remount ($name)"; fail=$((fail+1)); return
    fi
    if [ -f "$MNT/renamed.txt" ] && grep -q "hello from NeutrinoOS" "$MNT/renamed.txt"; then
        echo "PASS: $name: linux sees renamed.txt with content"; pass=$((pass+1))
    else
        echo "FAIL: $name: linux sees renamed.txt with content"; fail=$((fail+1))
    fi
    if [ -d "$MNT/neodir" ]; then
        echo "PASS: $name: linux sees neodir"; pass=$((pass+1))
    else
        echo "FAIL: $name: linux sees neodir"; fail=$((fail+1))
    fi
    if [ -f "$MNT/linuxdir/fromneo.dat" ] && grep -q "neo-into-linux-dir" "$MNT/linuxdir/fromneo.dat"; then
        echo "PASS: $name: linux sees fromneo.dat"; pass=$((pass+1))
    else
        echo "FAIL: $name: linux sees fromneo.dat"; fail=$((fail+1))
    fi
    if [ ! -f "$MNT/linuxdir/nested.bin" ]; then
        echo "PASS: $name: linux confirms nested.bin deleted"; pass=$((pass+1))
    else
        echo "FAIL: $name: linux confirms nested.bin deleted"; fail=$((fail+1))
    fi
    if grep -q "linux content" "$MNT/linux.txt"; then
        echo "PASS: $name: linux.txt preserved"; pass=$((pass+1))
    else
        echo "FAIL: $name: linux.txt preserved"; fail=$((fail+1))
    fi
    linux_unmount

    fsck.exfat "$img" | tee "$WORK/$name.fsck"
    check "$name: fsck clean" "$img: clean" "$WORK/$name.fsck"
}

run_variant default512 -L NEO512
run_variant cluster4k -L NEO4K -c 4K
run_variant cluster32k -L NEO32K -c 32K
run_variant cluster128k -L NEO128K -c 128K
run_variant packbitmap -L NEOPACK -c 4K --pack-bitmap

echo ""
echo "=== interop summary: $pass PASS / $fail FAIL ==="
if [ "$fail" -eq 0 ]; then
    echo "INTEROP: ALL-PASS"
    exit 0
fi
echo "INTEROP: HAS-FAILURES"
exit 1
