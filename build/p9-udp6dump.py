#!/usr/bin/env python3
# Dump IPv6/UDP frame headers + payloads from a pcap.
import struct, sys

path = sys.argv[1]
data = open(path, 'rb').read()
off = 24
idx = 0
while off + 16 <= len(data):
    ts, tus, incl, orig = struct.unpack('<IIII', data[off:off+16])
    off += 16
    frame = data[off:off+incl]
    off += incl
    if len(frame) < 14 or frame[12:14] != b'\x86\xdd':
        idx += 1
        continue
    ip6 = frame[14:]
    plen = struct.unpack('>H', ip6[4:6])[0]
    nh = ip6[6]
    src = ip6[8:24]
    dst = ip6[24:40]
    payload = ip6[40:40+plen]
    if nh == 17:
        sport, dport = struct.unpack('>HH', payload[0:4])
        ulen, = struct.unpack('>H', payload[4:6])
        print('frame %d UDP %s:%d -> %s:%d ulen=%d' % (
            idx, src.hex(), sport, dst.hex(), dport, ulen))
        body = payload[8:ulen]
        print('   payload[%d]: %s' % (len(body), body.hex()))
    idx += 1
