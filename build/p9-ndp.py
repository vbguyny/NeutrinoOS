#!/usr/bin/env python3
# Decode ICMPv6 RA / NS / NA payloads from a pcap.
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
        continue
    ip6 = frame[14:]
    plen = struct.unpack('>H', ip6[4:6])[0]
    nh = ip6[6]
    src = ip6[8:24]
    dst = ip6[24:40]
    payload = ip6[40:40+plen]
    t = payload[0] if payload else -1
    kind = {128:'echo-req',129:'echo-reply',133:'RS',134:'RA',135:'NS',136:'NA'}.get(t, 'type%d'%t)
    print('--- frame %d %s %s -> %s hl=%d' % (idx, kind, src.hex(), dst.hex(), ip6[7]))
    if kind == 'RA' and len(payload) >= 16:
        print('   cur_hl=%02x flags(M=%d O=%d)=%02x lifetime=%d reach=%d retrans=%d' % (
            payload[4], (payload[5]>>7)&1, (payload[5]>>6)&1, payload[5],
            struct.unpack('>H', payload[6:8])[0],
            struct.unpack('>I', payload[8:12])[0],
            struct.unpack('>I', payload[12:16])[0]))
        o = 16
        while o + 2 <= len(payload):
            otype = payload[o]; olen = payload[o+1]*8
            if olen == 0: break
            body = payload[o+2:o+olen]
            if otype == 1:
                print('   SLLAO len=%d mac=%s' % (olen, body.hex()))
            elif otype == 3 and len(body) >= 30:
                plen_ = body[0]
                flags = body[1]
                valid = struct.unpack('>I', body[2:6])[0]
                pref = struct.unpack('>I', body[6:10])[0]
                prefix = body[14:30]
                print('   PIO plen=%d flags=%02x (L=%d A=%d) valid=%d pref=%d prefix=%s' % (
                    plen_, flags, (flags>>7)&1, (flags>>6)&1, valid, pref, prefix.hex()))
            elif otype == 25:
                print('   RDNSS lifetime=%d addr=%s' % (struct.unpack('>I', body[0:4])[0], body[4:20].hex()))
            else:
                print('   opt type=%d len=%d raw=%s' % (otype, olen, payload[o:o+olen].hex()))
            o += olen
    elif kind in ('NS','NA') and len(payload) >= 24:
        flags = payload[4]
        target = payload[8:24].hex()
        print('   flags=%02x target=%s' % (flags, target))
    idx += 1
print('total', idx, 'frames')
