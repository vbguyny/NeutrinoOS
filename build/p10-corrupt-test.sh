#!/bin/bash
# Phase 10 fsck detect/repair tests with deliberate corruptions.
# usage: p10-corrupt-test.sh [harness-dir]
set -u
HOST_DIR="${1:-/mnt/d/Projects/Code/NeutrinoOS/tests/exfat-host}"
WORK=/tmp/p10corrupt
IMG="$WORK/vol.img"
H="$HOST_DIR/bin/Debug/net10.0/exfat-host"
PY=/mnt/d/Projects/Code/NeutrinoOS/build/p10-corrupt.py

mkdir -p "$WORK"
(cd "$HOST_DIR" && dotnet build -v q --nologo 2>&1 | grep -E "error" && exit 1) || true

pass=0
fail=0
check() {
    if grep -aq "$2" "$3" 2>/dev/null; then
        echo "PASS: $1"; pass=$((pass+1))
    else
        echo "FAIL: $1  ($(head -c 200 "$3" | tr '\n' ' '))"; fail=$((fail+1))
    fi
}

fresh() {
    rm -f "$IMG"
    "$H" "$IMG" format 32 FSCKTEST > /dev/null
    "$H" "$IMG" mkdir /dir > /dev/null
    "$H" "$IMG" write /file.txt "fsck fixture content" > /dev/null
    "$H" "$IMG" write /dir/inner.dat "inner fixture" > /dev/null
}

run_fsck() { "$H" "$IMG" fsck "$@" > "$WORK/fsck.out" 2>&1; }

echo "=== baseline: fsck on clean volume ==="
fresh
run_fsck
check "clean volume reports no errors" "errors=0" "$WORK/fsck.out"
check "clean verdict" "FSCK: CLEAN" "$WORK/fsck.out"

for mode in boot-checksum upcase-checksum set-checksum bitmap-orphan bitmap-missing dirty; do
    echo ""
    echo "=== corruption: $mode ==="
    fresh
    python3 "$PY" "$IMG" "$mode" > /dev/null

    run_fsck
    check "$mode: check detects a problem" "FSCK: PROBLEMS\|note: VolumeDirty" "$WORK/fsck.out"

    run_fsck repair
    check "$mode: repair reports repaired" "repaired=[1-9]" "$WORK/fsck.out"
    check "$mode: repair verdict clean" "FSCK: CLEAN" "$WORK/fsck.out"

    # Independent verification with the reference checker.
    fsck.exfat "$IMG" > "$WORK/ref.out" 2>&1
    check "$mode: exfatprogs agrees (clean)" "$IMG: clean" "$WORK/ref.out"

    # Data must still be readable after repair.
    "$H" "$IMG" cat /file.txt > "$WORK/cat.out" 2>&1
    check "$mode: fixture still readable" "fsck fixture content" "$WORK/cat.out"
done

echo ""
echo "=== label set/get ==="
fresh
"$H" "$IMG" label > "$WORK/label1.out" 2>&1
check "label read" 'label="FSCKTEST"' "$WORK/label1.out"
"$H" "$IMG" label NEWLABEL > "$WORK/label2.out" 2>&1
check "label set" "label set OK" "$WORK/label2.out"
"$H" "$IMG" label > "$WORK/label3.out" 2>&1
check "label readback" 'label="NEWLABEL"' "$WORK/label3.out"
fsck.exfat "$IMG" > "$WORK/ref.out" 2>&1
check "label change fsck clean" "$IMG: clean" "$WORK/ref.out"

echo ""
echo "=== attrib ==="
"$H" "$IMG" attrib /file.txt +rh > "$WORK/attrib1.out" 2>&1
check "attrib +rh applied" "attrib OK" "$WORK/attrib1.out"
"$H" "$IMG" stat /file.txt > "$WORK/stat1.out" 2>&1
check "stat shows ReadOnly+Hidden" "ReadOnly" "$WORK/stat1.out"
check "stat shows Hidden" "Hidden" "$WORK/stat1.out"
"$H" "$IMG" attrib /file.txt -r > "$WORK/attrib2.out" 2>&1
"$H" "$IMG" stat /file.txt > "$WORK/stat2.out" 2>&1
if grep -aq "ReadOnly" "$WORK/stat2.out"; then
    echo "FAIL: attrib -r did not clear ReadOnly"; fail=$((fail+1))
else
    echo "PASS: attrib -r cleared ReadOnly"; pass=$((pass+1))
fi
fsck.exfat "$IMG" > "$WORK/ref.out" 2>&1
check "attrib change fsck clean" "$IMG: clean" "$WORK/ref.out"

echo ""
echo "=== corrupt summary: $pass PASS / $fail FAIL ==="
if [ "$fail" -eq 0 ]; then
    echo "CORRUPT: ALL-PASS"
    exit 0
fi
echo "CORRUPT: HAS-FAILURES"
exit 1
