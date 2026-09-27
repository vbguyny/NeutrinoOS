#!/bin/bash
# Phase 10 host harness smoke: mount/format/write/list/rename/delete + fsck.
# usage: p10-host-smoke.sh [harness-dir] [image]
# Verdict: "SMOKE: ALL-PASS" / "SMOKE: HAS-FAILURES"
# (the second "mkdir /docs" intentionally reports AlreadyExists - the
#  harness Check prints FAIL for it and it is whitelisted below)
set -u
HOST_DIR="${1:-/mnt/d/Projects/Code/NeutrinoOS/tests/exfat-host}"
IMG="${2:-/tmp/p10smoke.img}"
H="$HOST_DIR/bin/Debug/net10.0/exfat-host"
RUNLOG=/tmp/p10smoke-run.log
rm -f "$RUNLOG"

(cd "$HOST_DIR" && dotnet build -v q --nologo 2>&1 | grep -E "error" && exit 1) || true

run() { "$H" "$IMG" "$@" 2>&1 | tee -a "$RUNLOG"; }

echo "=== format ==="
rm -f "$IMG"
run format 32 NEOFMT || exit 1
echo "=== fsck after format ==="
fsck.exfat "$IMG" || exit 1

echo "=== mkdir (twice: second reports AlreadyExists) ==="
run mkdir /docs | tail -1
run mkdir /docs | tail -1
echo "=== write ==="
run write /hello.txt hello-from-neutrino | tail -2
run write /docs/note.txt note-content | tail -2
echo "=== list root ==="
run list /
echo "=== stat ==="
run stat /hello.txt | tail -2
echo "=== rename ==="
run mv /hello.txt /renamed.txt | tail -3
echo "=== delete ==="
run rm /renamed.txt | tail -1
run rm /docs/note.txt | tail -1
run rmdir /docs | tail -1
echo "=== big file ==="
run big /big.bin 2 | tail -4
echo "=== fill 200 files ==="
run mkdir /many | tail -1
run fill /many 200 | tail -3
echo "=== long name ==="
run longname / | tail -4
echo "=== final list ==="
run list / | tail -4

echo "=== fsck after writes ==="
fsck.exfat "$IMG"
fsck_rc=$?

fails=$(grep -c '^FAIL' "$RUNLOG" || true)
known=$(grep -c '^FAIL: mkdir /docs (AlreadyExists)' "$RUNLOG" || true)
if [ "$fails" = "$known" ] && [ "$known" = "1" ] && [ "$fsck_rc" = "0" ]; then
  echo "SMOKE: ALL-PASS"
else
  echo "SMOKE: HAS-FAILURES (unexpected FAIL lines or fsck rc=$fsck_rc)"
  grep '^FAIL' "$RUNLOG" | grep -v 'mkdir /docs (AlreadyExists)' || true
  exit 1
fi
echo "SMOKE_DONE"
