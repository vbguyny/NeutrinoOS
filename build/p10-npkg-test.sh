#!/bin/bash
# Phase 10 npkg acceptance: on-device install of neutrinoos.utils.exfat.
#
#  1. The four tools are absent from /bin (deploy omitted them).
#  2. `npkg repo add local /repo` trusts the signed index; search finds
#     neutrinoos.utils.exfat; install succeeds; verify passes; list shows
#     the package.
#  3. The installed tools run from /bin: mkexfat formats a blank disk
#     (hdb) and fsck.exfat + exfatlabel operate on it.
#
# Prereqs: bash build/p10-package.sh && bash build/p10-npkg-deploy.sh
# Log: /root/p10npkg.log   Verdict: "=== p10npkg summary: ALL-PASS ==="
set -u
SRC=/mnt/d/Projects/Code/NeutrinoOS
IMG=/root/p10npkg.img
FMT=/root/p10npkg-fmt.img
LOG=/root/p10npkg.log

pkill -9 qemu-system 2>/dev/null || true
sleep 1

PASS=0; FAIL=0
check() {
  if [ "$1" = "1" ]; then echo "PASS: $2"; PASS=$((PASS+1));
  else echo "FAIL: $2"; FAIL=$((FAIL+1)); fi
}

test -f "$IMG" || { echo "missing $IMG - run build/p10-npkg-deploy.sh"; exit 1; }
[ -f "$FMT" ] || dd if=/dev/zero of="$FMT" bs=1M count=32 status=none

cp -f /usr/share/OVMF/OVMF_VARS_4M.fd /root/neutrino/build/x64/OVMF_VARS-ahci.fd
rm -f /root/qin "$LOG"
mkfifo /root/qin

( tail -f /root/qin | timeout 300 qemu-system-x86_64 -machine q35 -m 2G -cpu max -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/root/neutrino/build/x64/OVMF_VARS-ahci.fd \
  -drive id=bootdisk,if=none,format=raw,file="$IMG" \
  -device ide-hd,drive=bootdisk,bus=ide.0 \
  -drive id=fmtblank,if=none,format=raw,file="$FMT" \
  -device ide-hd,drive=fmtblank,bus=ide.1 \
  -display none -serial stdio -no-reboot -no-shutdown > "$LOG" 2>&1 ) &

echo "waiting for shell..."
for i in $(seq 1 120); do
  if strings "$LOG" 2>/dev/null | grep -q 'root-/> '; then echo "prompt at ${i}s"; break; fi
  sleep 1
done
sleep 2

cmd() { echo "$1" > /root/qin; sleep "${2:-3}"; }

# 1. tools absent before install
cmd "fsck.exfat /dev/hdb" 3

# 2. install via npkg
cmd "npkg repo add local /repo" 3
cmd "npkg search exfat" 3
cmd "npkg install neutrinoos.utils.exfat" 6
cmd "npkg list" 3
cmd "npkg verify /repo/neutrinoos.utils.exfat-1.0.0.npkg" 4

# 3. run the installed tools
cmd "mkexfat -L FROMNPKG /dev/hdb" 6
cmd "fsck.exfat /dev/hdb" 4
cmd "exfatlabel /dev/hdb" 3
cmd "exit" 2

pkill -9 qemu-system 2>/dev/null || true
sleep 1

echo "=== checking transcript ==="
T=$(strings "$LOG" | grep -v '^\[j|')
echo "$T" | grep -qE "fsck.exfat: command not found" && check 1 "tools absent before install" || check 0 "tools absent before install"
echo "$T" | grep -q "added repository local" && check 1 "npkg repo add local /repo" || check 0 "npkg repo add local /repo"
echo "$T" | grep -q "neutrinoos.utils.exfat" && check 1 "npkg search finds the package" || check 0 "npkg search finds the package"
echo "$T" | grep -q "installed neutrinoos.utils.exfat 1.0.0" && check 1 "npkg install succeeded" || check 0 "npkg install succeeded"
echo "$T" | grep -q "formatted hdb (exFAT" && check 1 "mkexfat (installed) formatted hdb" || check 0 "mkexfat (installed) formatted hdb"
echo "$T" | grep -q "fsck.exfat: clean" && check 1 "fsck.exfat (installed) reports clean" || check 0 "fsck.exfat (installed) reports clean"

echo "=== verifying the formatted disk in Linux ==="
LBL=$(/usr/sbin/exfatlabel "$FMT" 2>/dev/null)
echo "$LBL" | grep -q "FROMNPKG" && check 1 "hdb label FROMNPKG (set by installed mkexfat)" || { echo "label: $LBL"; check 0 "hdb label FROMNPKG (set by installed mkexfat)"; }
fsck.exfat "$FMT" > /root/p10npkg-fsck.log 2>&1
grep -q "clean" /root/p10npkg-fsck.log && check 1 "hdb fsck clean (exfatprogs)" || check 0 "hdb fsck clean (exfatprogs)"

echo
echo "=== p10npkg summary: $PASS PASS / $FAIL FAIL ==="
[ $FAIL -eq 0 ] && echo "P10NPKG: ALL-PASS" || echo "P10NPKG: HAS-FAILURES"
exit $FAIL
