#!/usr/bin/env python3
"""Dump an exFAT boot sector's key fields (NeutrinoOS Phase 10 test aid)."""
import struct
import sys

path = sys.argv[1]
d = open(path, 'rb').read()
print("volumeLength", struct.unpack_from('<Q', d, 72)[0])
print("fatOffset", struct.unpack_from('<I', d, 80)[0],
      "fatLength", struct.unpack_from('<I', d, 84)[0])
print("heapOffset", struct.unpack_from('<I', d, 88)[0],
      "clusterCount", struct.unpack_from('<I', d, 92)[0])
print("rootCluster", struct.unpack_from('<I', d, 96)[0])
print("serial %08X" % struct.unpack_from('<I', d, 100)[0],
      "rev %04X" % struct.unpack_from('<H', d, 104)[0],
      "flags %04X" % struct.unpack_from('<H', d, 106)[0])
print("sectorShift", d[108], "clusterShift", d[109], "fats", d[110], "pct", d[112])

sector_size = 1 << d[108]
stored = struct.unpack_from('<I', d, 11 * sector_size)[0]
c = 0
for i in range(11 * sector_size):
    if i in (106, 107, 112):
        continue
    c = (((c << 31) | (c >> 1)) & 0xFFFFFFFF)
    c = (c + d[i]) & 0xFFFFFFFF
print("boot checksum stored=%08X computed=%08X %s" % (stored, c, "OK" if stored == c else "BAD"))

# Root directory first entries (cluster 2-based heap math).
heap = struct.unpack_from('<I', d, 88)[0]
spc = 1 << d[109]
root = struct.unpack_from('<I', d, 96)[0]
off = (heap + (root - 2) * spc) * sector_size
for i in range(5):
    e = d[off + i * 32: off + (i + 1) * 32]
    print("root[%d] type=%02X %s" % (i, e[0], e.hex()))

# FAT head.
fat_off = struct.unpack_from('<I', d, 80)[0] * sector_size
entries = struct.unpack_from('<8I', d, fat_off)
print("FAT[0..7] =", [hex(x) for x in entries])
