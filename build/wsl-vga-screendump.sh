#!/usr/bin/env bash
# Visual check for VGA ring (display-start) scrolling:
# boots the CLI image with -vga std (headless), drives the shell through
# the QEMU monitor via PS/2 sendkey, forces many scrolls + a ring
# compaction (seq 1 5000 ~= 208 scrolls at 25 rows), and takes PPM
# screendumps that are converted to PNG for viewing.
set -e
cd /mnt/d/Projects/Code/NeutrinoOS

pkill -f qemu-system-x86_64 2>/dev/null || true
sleep 1

IMG=build/neutrinoos-cli.img
cp /usr/share/OVMF/OVMF_VARS_4M.fd /tmp/VARS-vga.fd
rm -f /tmp/vga-ser.log /tmp/sd*.ppm /tmp/sd*.png /tmp/mon.log

qemu-system-x86_64 -machine q35 -m 2G -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
  -drive if=pflash,format=raw,file=/tmp/VARS-vga.fd \
  -drive format=raw,file=$IMG \
  -vga std -display none \
  -monitor telnet:127.0.0.1:4444,server,nowait \
  -serial file:/tmp/vga-ser.log \
  -no-reboot -no-shutdown >>/tmp/mon.log 2>&1 &
QPID=$!

mon() {
  exec 3<>/dev/tcp/127.0.0.1/4444 || return 1
  printf '%s\r\n' "$1" >&3
  sleep 0.15
  exec 3<&- 3>&- || true
}

# Wait for the shell prompt (serial mirrors the VGA console)
for i in $(seq 1 80); do
  grep -q 'user@neutrinoos' /tmp/vga-ser.log 2>/dev/null && break
  sleep 0.5
done
sleep 2

echo "=== screendump 0: boot screen"
mon "screendump /tmp/sd0.ppm"
sleep 1

echo "=== typing: seq 1 5000 (forces ~208 scrolls + ring compaction)"
for k in s e q spc 1 spc 5 0 0 0 ret; do mon "sendkey $k"; done
sleep 4
mon "screendump /tmp/sd1.ppm"
sleep 1

echo "=== typing: ls /bin (more scrolls after compaction)"
for k in l s spc / b i n ret; do mon "sendkey $k"; done
sleep 2
mon "screendump /tmp/sd2.ppm"
sleep 1

kill $QPID 2>/dev/null || true
sleep 1
pkill -f qemu-system-x86_64 2>/dev/null || true

# ---- PPM (P6) -> PNG converter, zlib only ----
python3 - <<'PY'
import zlib, struct, glob

def chunk(t, d):
    return (struct.pack('>I', len(d)) + t + d +
            struct.pack('>I', zlib.crc32(t + d) & 0xffffffff))

def ppm_to_png(src, dst):
    with open(src, 'rb') as f:
        data = f.read()
    parts = data.split(b'\n', 3)
    assert parts[0] == b'P6', parts[0]
    w, h = map(int, parts[1].split())
    body = parts[3]
    raw = b''.join(b'\x00' + body[y*w*3:(y+1)*w*3] for y in range(h))
    png = (b'\x89PNG\r\n\x1a\n' +
           chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) +
           chunk(b'IDAT', zlib.compress(raw, 9)) +
           chunk(b'IEND', b''))
    with open(dst, 'wb') as f:
        f.write(png)

for src in sorted(glob.glob('/tmp/sd*.ppm')):
    dst = src[:-4] + '.png'
    ppm_to_png(src, dst)
    print(dst, 'ok')
PY

cp /tmp/sd*.png /mnt/d/Projects/Code/NeutrinoOS/build/ 2>/dev/null || true
echo "=== serial tail:"
tail -12 /tmp/vga-ser.log
ls -la /tmp/sd*.png
