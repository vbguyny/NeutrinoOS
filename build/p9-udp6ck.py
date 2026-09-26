#!/usr/bin/env python3
# Validate IPv6 UDP checksums in a pcap capture with an independent
# implementation.
import struct, sys

path = sys.argv[1]
data = open(path, 'rb').read()
endian = '<' if struct.unpack('<I', data[:4])[0] not in (0xd4c3b2a1,) else '>'
off = 24
def csum16(b, init=0):
    s = init
    if len(b) % 2:
        b = b + b'\x00'
    for i in range(0, len(b), 2):
        s += (b[i] << 8) | b[i+1]
    return s

n = 0
while off + 16 <= len(data):
    ts, tus, incl, orig = struct.unpack(endian + 'IIII', data[off:off+16])
    off += 16
    frame = data[off:off+incl]
    off += incl
    if len(frame) < 14 or frame[12:14] != b'\x86\xdd':
        continue
    ip6 = frame[14:]
    plen = struct.unpack('>H', ip6[4:6])[0]
    nh = ip6[6]
    src = ip6[8:24]
    dst = ip6[24:40]
    payload = ip6[40:40+plen]
    if nh != 17:
        print('frame %d nh=%d (skipped)' % (n, nh)); n += 1; continue
    udp = payload
    stored = struct.unpack('>H', udp[6:8])[0]
    zeroed = udp[:6] + b'\x00\x00' + udp[8:]
    # pseudo header: src + dst + length(4) + zeros(3) + next header
    pseudo = src + dst + struct.pack('>I', len(udp)) + b'\x00\x00\x00' + bytes([nh])
    s = csum16(zeroed, 0) + csum16(pseudo, 0)
    while s >> 16:
        s = (s & 0xFFFF) + (s >> 16)
    calc = (~s) & 0xFFFF
    print('frame %d udp6 stored=%04x calc=%04x %s' % (n, stored, calc, 'OK' if stored == calc else 'MISMATCH'))
    n += 1
print('checked', n)
