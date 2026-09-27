#!/usr/bin/env python3
"""Corrupt specific exFAT structures for fsck tests (NeutrinoOS Phase 10).

usage: p10-corrupt.py <image> <mode>
  boot-checksum    flip one BootCode byte (checksum mismatch, geometry intact)
  upcase-checksum  flip one byte of the up-case table entry checksum field
  set-checksum     flip the SetChecksum of the first file entry in the root
  bitmap-orphan    mark one free cluster allocated (orphan)
  bitmap-missing   mark one allocated cluster free (missing allocation)
  dirty            set the VolumeDirty flag
"""
import struct
import sys

path, mode = sys.argv[1], sys.argv[2]
d = bytearray(open(path, 'rb').read())
sect = 1 << d[108]
fat_off = struct.unpack_from('<I', d, 80)[0]
heap = struct.unpack_from('<I', d, 88)[0]
spc = 1 << d[109]
root = struct.unpack_from('<I', d, 96)[0]
cluster_count = struct.unpack_from('<I', d, 92)[0]
clu_sect = lambda c: heap + (c - 2) * spc


def root_dir_offset():
    return clu_sect(root) * sect


def find_entry(type_byte):
    off = root_dir_offset()
    for i in range(256):
        e = d[off + i * 32: off + (i + 1) * 32]
        if e[0] == 0x00:
            return None
        if e[0] == type_byte:
            return off + i * 32
    return None


if mode == 'boot-checksum':
    d[400] ^= 0xFF          # BootCode byte; excluded from nothing, breaks checksum
    print("flipped BootCode byte 400")
elif mode == 'upcase-checksum':
    e = find_entry(0x82)
    d[e + 4] ^= 0xFF
    print("flipped upcase checksum low byte at", e + 4)
elif mode == 'set-checksum':
    off = root_dir_offset()
    for i in range(256):
        e = off + i * 32
        if d[e] == 0x00:
            break
        if d[e] == 0x85:
            d[e + 2] ^= 0xFF
            print("flipped set checksum at entry", i)
            break
    else:
        print("no file entry found")
        sys.exit(1)
elif mode == 'bitmap-orphan':
    e = find_entry(0x81)
    bc = struct.unpack_from('<I', d, e + 20)[0]
    blen = struct.unpack_from('<Q', d, e + 24)[0]
    boff = clu_sect(bc) * sect
    # Find a free bit near the end of the bitmap and set it.
    for idx in range(len(d) and int(blen) - 1, 0, -1):
        if d[boff + idx] == 0x00:
            d[boff + idx] = 0x01
            print("orphan bit set at bitmap byte", idx)
            break
    else:
        print("no free bit found")
        sys.exit(1)
elif mode == 'bitmap-missing':
    e = find_entry(0x81)
    bc = struct.unpack_from('<I', d, e + 20)[0]
    boff = clu_sect(bc) * sect
    # Cluster 2 (bitmap itself) is definitely allocated: clear its bit.
    d[boff] &= 0xFE
    print("cleared bit for cluster 2 (missing allocation)")
elif mode == 'dirty':
    flags = struct.unpack_from('<H', d, 106)[0] | 0x0002
    struct.pack_into('<H', d, 106, flags)
    print("set VolumeDirty")
else:
    print("unknown mode", mode)
    sys.exit(2)

open(path, 'wb').write(d)
print("corruption applied:", mode)
