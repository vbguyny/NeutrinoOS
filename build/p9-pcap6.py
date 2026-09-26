#!/usr/bin/env python3
# List Ethernet/IPv6/ICMPv6 frames from a pcap (filter-dump format).
import struct, sys

path = sys.argv[1]
data = open(path, 'rb').read()
magic = struct.unpack('<I', data[:4])[0]
if magic == 0xa1b2c3d4 or magic == 0xa1b2c3d4:
    endian = '<'
elif magic == 0xd4c3b2a1:
    endian = '>'
else:
    # filter-dump uses native little endian magic 0xa1b2c3d4 typically
    endian = '<'

off = 24
n = 0
while off + 16 <= len(data):
    ts, tus, incl, orig = struct.unpack(endian + 'IIII', data[off:off+16])
    off += 16
    frame = data[off:off+incl]
    off += incl
    if len(frame) < 14:
        continue
    eth = frame[12:14]
    if eth == b'\x86\xdd':
        if len(frame) < 14 + 40:
            continue
        ip6 = frame[14:]
        nh = ip6[6]
        src = ':'.join('%02x%02x' % (ip6[8+i*2], ip6[9+i*2]) for i in range(8))
        dst = ':'.join('%02x%02x' % (ip6[24+i*2], ip6[25+i*2]) for i in range(8))
        extra = ''
        if nh == 58 and len(ip6) >= 44:
            t = ip6[40]
            names = {128:'echo-req',129:'echo-rep',133:'RS',134:'RA',135:'NS',136:'NA',137:'redirect'}
            extra = 'icmp6=%s' % names.get(t, 'type%d' % t)
        print('%4d  nh=%3d  %s -> %s  %s' % (n, nh, src, dst, extra))
        n += 1
print('total ipv6 frames:', n)
