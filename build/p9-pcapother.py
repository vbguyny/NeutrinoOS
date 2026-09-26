#!/usr/bin/env python3
# List non-IPv6 frames (ARP etc.) in a pcap.
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
    if len(frame) < 14:
        idx += 1
        continue
    et = struct.unpack('>H', frame[12:14])[0]
    if et != 0x86DD:
        extra = ''
        dmac = ':'.join('%02x' % b for b in frame[0:6])
        if et == 0x0806 and len(frame) >= 42:
            op = struct.unpack('>H', frame[20:22])[0]
            spa = '.'.join(str(b) for b in frame[28:32])
            tpa = '.'.join(str(b) for b in frame[38:42])
            smac = ':'.join('%02x' % b for b in frame[22:28])
            hrd = struct.unpack('>H', frame[14:16])[0]
            pro = '0x%04x' % struct.unpack('>H', frame[16:18])[0]
            extra = ' ARP op=%d hrd=%d pro=%s spa=%s tpa=%s smac=%s' % (
                op, hrd, pro, spa, tpa, smac)
        elif et == 0x0800 and len(frame) >= 34:
            proto = frame[23]
            extra = ' IPv4 proto=%d' % proto
        print('frame %d len=%d dst=%s ethertype=0x%04x%s' % (idx, len(frame), dmac, et, extra))
    idx += 1
print('total', idx)
