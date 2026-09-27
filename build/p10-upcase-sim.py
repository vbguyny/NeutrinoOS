#!/usr/bin/env python3
"""Simulate the exFAT up-case decompression on the volume's table (Phase 10 test aid)."""
import sys

path = sys.argv[1]
heap = None
d = open(path, 'rb').read()
import struct
heap = struct.unpack_from('<I', d, 88)[0]
spc = 1 << d[109]
sect = 1 << d[108]
root = None
# find upcase entry in root to get cluster/length (root from boot)
root = struct.unpack_from('<I', d, 96)[0]
root_off = (heap + (root - 2) * spc) * sect
upcase_cluster = upcase_length = None
for i in range(64):
    e = d[root_off + i * 32: root_off + (i + 1) * 32]
    if e[0] == 0x00:
        break
    if e[0] == 0x82:
        upcase_cluster = struct.unpack_from('<I', e, 20)[0]
        upcase_length = struct.unpack_from('<Q', e, 24)[0]
print("upcase cluster=%s length=%s" % (upcase_cluster, upcase_length))

off = (heap + (upcase_cluster - 2) * spc) * sect
table = d[off:off + upcase_length]
pos = 0
out = 0
bad = None
while pos + 1 < len(table) and out < 65536:
    v = table[pos] | (table[pos + 1] << 8)
    pos += 2
    if v == 0xFFFF:
        if pos + 3 >= len(table):
            bad = ("truncated-run", pos)
            break
        cnt = table[pos] | (table[pos + 1] << 8)
        m = table[pos + 2] | (table[pos + 3] << 8)
        pos += 4
        out += cnt
    else:
        out += 1
print("parsed pos=%d out=%d bad=%s" % (pos, out, bad))
print("last8:", table[-8:].hex())
