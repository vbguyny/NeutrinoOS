#!/usr/bin/env python3
"""Verify both exFAT boot region checksums (NeutrinoOS Phase 10 test aid)."""
import struct
import sys

d = open(sys.argv[1], 'rb').read()
s = 1 << d[108]


def compute(base):
    c = 0
    for i in range(11 * s):
        if i in (106, 107, 112):
            continue
        c = (((c << 31) | (c >> 1)) & 0xFFFFFFFF)
        c = (c + d[base + i]) & 0xFFFFFFFF
    return c


stored_main = struct.unpack_from('<I', d, 11 * s)[0]
stored_back = struct.unpack_from('<I', d, 23 * s)[0]
c_main = compute(0)
c_back = compute(12 * s)
print("main:   stored=%08X computed=%08X %s" % (stored_main, c_main, "OK" if stored_main == c_main else "BAD"))
print("backup: stored=%08X computed=%08X %s" % (stored_back, c_back, "OK" if stored_back == c_back else "BAD"))
# The checksum sector should repeat the value across the whole sector.
rep1 = struct.unpack_from('<I', d, 11 * s + 4)[0]
print("main checksum repeated: %s" % ("OK" if rep1 == stored_main else "BAD(%08X)" % rep1))
