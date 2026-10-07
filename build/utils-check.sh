#!/bin/bash
# Test every /bin utility in the CLI image:
#  - every utility must answer `--help` (or -h) with a usage message
#  - a functional sweep exercises the safe read-only / filesystem / network
#    utilities and records any errors
# Boots a COPY of the image with a virtio NIC (QEMU user-net).
set -u
cd /root/neutrino
pkill -9 -f qin-utils 2>/dev/null || true
pkill -9 -f qemu-system 2>/dev/null || true
sleep 1

IMG=build/x64/neutrinoos-cli.img
cp -f "$IMG" /tmp/utils-check.img
cp -f /usr/share/OVMF/OVMF_VARS_4M.fd build/x64/OVMF_VARS-utils.fd
rm -f /root/qin-utils /root/utils-qemu.out /root/utils-problems.txt
touch /root/qin-utils

setsid bash -c "tail -f /root/qin-utils | qemu-system-x86_64 -machine q35 -m 2G -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=build/x64/OVMF_VARS-utils.fd \
  -drive format=raw,file=/tmp/utils-check.img \
  -netdev user,id=n0,hostfwd=tcp::2222-:22,hostfwd=tcp::18080-:80 \
  -device virtio-net-pci,netdev=n0 \
  -vga std -display none -serial stdio -no-reboot -no-shutdown" < /dev/null > /root/utils-qemu.out 2>&1 &

prompt_count() { local n; n=$(grep -ac 'root-/>' /root/utils-qemu.out 2>/dev/null); echo "${n:-0}"; }

booted=0
for i in $(seq 1 120); do
    if [ "$(prompt_count)" -gt 0 ]; then booted=1; break; fi
    sleep 1
done
if [ "$booted" != 1 ]; then
    echo "BOOT FAILED - log tail:"
    tail -c 600 /root/utils-qemu.out | tr -d '\r'
    pkill -9 -f qemu-system 2>/dev/null || true
    exit 1
fi

send() {
    local cmd="$1"
    local before after waited=0
    before=$(prompt_count)
    printf '%s\n' "$cmd" >> /root/qin-utils
    while [ "$waited" -lt 200 ]; do
        sleep 0.3
        waited=$((waited + 1))
        after=$(prompt_count)
        if [ "$after" -gt "$before" ]; then return 0; fi
    done
    echo "TIMEOUT(${cmd})" >> /root/utils-problems.txt
    return 1
}

ALL="cat clear cp cryptotest curl date dbgtest df dhcp dhcp6 dns dns6 du echo env exfatattrib exfatlabel find free fsck.exfat grep h2test head hexdump ifconfig kill ls mkdir mkexfat more mount mv netstat npkg ping ping6 ps rm sampleapi seq sleep socktest sort ssh sshd startup tail tee touch tree umount uname uptime wc webapi webhost wget which whoami"

echo "boot ok; starting help sweep..."
send "cd /" || true
for u in $ALL; do
    send "$u --help" || true
done

echo "functional sweep..."
send "echo hello world"
send "date"
send "uname -a"
send "uptime"
send "env"
send "free"
send "df"
send "ps"
send "mount"
send "ifconfig"
send "netstat"
send "ls -l /etc"
send "ls -a /etc"
send "cat /etc/profile"
send "head -n 1 /etc/profile"
send "tail -n 1 /etc/profile"
send "wc /etc/profile"
send "grep PATH /etc/profile"
send "find /etc"
send "touch /tmp/t1.txt"
send "mkdir /tmp/d1"
send "cp /etc/profile /tmp/prof.bak"
send "ls /tmp"
send "mv /tmp/prof.bak /tmp/prof2.bak"
send "cat /tmp/prof2.bak"
send "rm /tmp/t1.txt"
send "rm /tmp/d1"
send "sleep 1"
send "kill 999999"
send "npkg list"
send "startup"
send "dhcp"
send "dns google.com"
send "webhost start"
send "h2test"
send "curl http://127.0.0.1/health"
send "wget http://127.0.0.1/health"
send "webhost stop"
send "sshd status"
send "sshd start"
send "sshd stop"
send "cryptotest"
send "dbgtest"
send "more /etc/profile"
send "seq 3"
send "seq 8 -2 2"
send "sort /etc/profile"
send "cat /etc/profile | tee /copy.txt"
send "cat /copy.txt"
send "hexdump -n 64 /etc/profile"
send "tree /etc"
send "du /bin"
send "which ls"
send "which cd"
send "whoami"
send "clear"

sleep 1
pkill -9 -f qemu-system 2>/dev/null || true
sleep 1

echo
echo "=== text utilities answer --help? ==="
miss=0
for u in $ALL; do
    if ! grep -a -A10 "^root-/> $u --help" /root/utils-qemu.out 2>/dev/null | grep -qi "usage"; then
        echo "NO-HELP: $u"
        miss=$((miss + 1))
    fi
done
[ "$miss" = 0 ] && echo "(all utilities printed a usage line)"

echo
echo "=== error scan (functional sweep) ==="
grep -a -nE 'ERROR|Unhandled|neutrinoos:|unknown option|\[run\] error|exception' /root/utils-qemu.out | head -40
echo "(end error scan)"

echo
echo "=== timeout markers ==="
cat /root/utils-problems.txt 2>/dev/null || echo "(none)"

echo
echo "=== tail ==="
tail -c 900 /root/utils-qemu.out | tr -d '\r'
