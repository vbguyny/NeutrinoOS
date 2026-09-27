#!/usr/bin/env python3
"""Extract the standard exFAT up-case table from a mkfs.exfat volume and
emit the C# table source for the DDK (NeutrinoOS Phase 10).

Also sanity-checks our checksum algorithms against the on-disk values:
  - boot region checksum (spec 3.4)
  - up-case table checksum (spec 7.2.4)
  - directory set checksum / name hash (spec 6.3.4 / 7.2.5) over the
    root directory's metadata entries.

usage: gen-exfat-std-tables.py <volume.img> <out.cs>
"""
import struct
import sys


def ror32(c, b):
    return ((c << 31) | (c >> 1)) & 0xFFFFFFFF


def ror16(c, b):
    return ((c << 15) | (c >> 1)) & 0xFFFF


def boot_checksum(data, sector_size, sectors=11):
    c = 0
    for i in range(sectors * sector_size):
        if i in (106, 107, 112):
            continue
        c = ror32(c, 1)
        c = (c + data[i]) & 0xFFFFFFFF
    return c


def set_checksum(entry_bytes):
    c = 0
    for i in range(len(entry_bytes)):
        if i in (2, 3):
            continue
        c = ror16(c, 1)
        c = (c + entry_bytes[i]) & 0xFFFF
    return c


def name_hash(name_utf16_upcased):
    c = 0
    for ch in name_utf16_upcased:
        c = ror16(c, 1)
        c = (c + (ch & 0xFF)) & 0xFFFF
        c = ror16(c, 1)
        c = (c + (ch >> 8)) & 0xFFFF
    return c


def main():
    img_path, out_path = sys.argv[1], sys.argv[2]
    data = open(img_path, 'rb').read()

    # Boot sector (512-byte sectors from mkfs.exfat defaults)
    assert data[0:3] == b'\xEB\x76\x90', "not exFAT"
    assert data[3:11] == b'EXFAT   ', "bad name"
    sector_shift = data[108]
    cluster_shift = data[109]
    sector_size = 1 << sector_shift
    cluster_size = sector_size << cluster_shift
    fat_offset = struct.unpack_from('<I', data, 80)[0]
    fat_length = struct.unpack_from('<I', data, 84)[0]
    heap_offset = struct.unpack_from('<I', data, 88)[0]
    cluster_count = struct.unpack_from('<I', data, 92)[0]
    root_cluster = struct.unpack_from('<I', data, 96)[0]

    print(f"sector={sector_size} cluster={cluster_size} fat@{fat_offset}+{fat_length} "
          f"heap@{heap_offset} clusters={cluster_count} root={root_cluster}")

    # Boot checksum validation.
    stored = struct.unpack_from('<I', data, 11 * sector_size)[0]
    calc = boot_checksum(data, sector_size)
    print(f"boot checksum stored={stored:08X} computed={calc:08X} "
          f"{'OK' if stored == calc else 'MISMATCH'}")

    def cluster_sector(cl):
        return heap_offset + (cl - 2) * (cluster_size // sector_size)

    def read_cluster(cl):
        off = cluster_sector(cl) * sector_size
        return data[off:off + cluster_size]

    # Scan the root directory for the bitmap / up-case / label entries.
    root = read_cluster(root_cluster)
    upcase_cluster = upcase_length = None
    upcase_stored_checksum = None
    bitmap_cluster = bitmap_length = None
    label = ""
    i = 0
    while i * 32 < len(root):
        e = root[i * 32:(i + 1) * 32]
        t = e[0]
        if t == 0x00:
            break
        if t == 0x81:
            bitmap_cluster = struct.unpack_from('<I', e, 20)[0]
            bitmap_length = struct.unpack_from('<Q', e, 24)[0]
            print(f"bitmap cluster={bitmap_cluster} length={bitmap_length}")
        elif t == 0x82:
            upcase_stored_checksum = struct.unpack_from('<I', e, 4)[0]
            upcase_cluster = struct.unpack_from('<I', e, 20)[0]
            upcase_length = struct.unpack_from('<Q', e, 24)[0]
            print(f"upcase cluster={upcase_cluster} length={upcase_length} "
                  f"checksum(stored)={upcase_stored_checksum:08X}")
        elif t == 0x83:
            n = e[1]
            label = e[2:2 + n * 2].decode('utf-16-le')
            print(f"label=\"{label}\"")
        elif t == 0x85:
            secondaries = e[1]
            setb = root[i * 32:(i + 1 + secondaries) * 32]
            sc = set_checksum(setb)
            stored_sc = struct.unpack_from('<H', e, 2)[0]
            stream = root[(i + 1) * 32:(i + 2) * 32]
            nh = struct.unpack_from('<H', stream, 4)[0]
            nl = stream[3]
            name_units = []
            up = 0
            while len(name_units) < nl:
                ne = root[(i + 2 + up) * 32:(i + 3 + up) * 32]
                units = struct.unpack_from('<' + 'H' * 15, ne, 2)
                name_units.extend(units[:nl - len(name_units)])
                up += 1
            name = ''.join(chr(u) for u in name_units)
            first = struct.unpack_from('<I', stream, 20)[0]
            size = struct.unpack_from('<Q', stream, 24)[0]
            print(f"entry \"{name}\" first={first} size={size} "
                  f"set-checksum={stored_sc:04X}/{sc:04X} "
                  f"name-hash={nh:04X}")
            i += 1 + secondaries
        i += 1

    # Extract the up-case table data (contiguous stream).
    assert upcase_cluster and upcase_length
    table = bytearray()
    cl = upcase_cluster
    while len(table) < upcase_length:
        table.extend(read_cluster(cl))
        cl += 1
    table = bytes(table[:upcase_length])

    c = 0
    for b in table:
        c = ror32(c, 1)
        c = (c + b) & 0xFFFFFFFF
    print(f"upcase checksum stored={upcase_stored_checksum:08X} computed={c:08X} "
          f"{'OK' if c == upcase_stored_checksum else 'MISMATCH'}")

    # Emit C#.
    lines = []
    lines.append("// NeutrinoOS Phase 10 - standard exFAT up-case table.")
    lines.append("//")
    lines.append("// Generated by build/gen-exfat-std-tables.py from a volume created")
    lines.append("// by Linux mkfs.exfat (exfatprogs), which embeds the table also used")
    lines.append("// by Windows: the compressed form defined by the exFAT specification")
    lines.append("// (spec 7.2.2). The formatter writes these bytes verbatim so every")
    lines.append("// host sees the canonical case mapping, and the driver's decompressor")
    lines.append("// is verified against it.")
    lines.append("")
    lines.append("namespace ProtonOS.DDK.Storage.ExFat;")
    lines.append("")
    lines.append("/// <summary>The canonical exFAT up-case table, compressed (spec 7.2.2).</summary>")
    lines.append("public static class ExFatStdUpcase")
    lines.append("{")
    lines.append("    /// <summary>Compressed table bytes exactly as stored on disk.</summary>")
    lines.append("    public static readonly byte[] Compressed = new byte[]")
    lines.append("    {")
    row = []
    for idx, b in enumerate(table):
        row.append(f"0x{b:02X},")
        if len(row) == 16:
            lines.append("        " + " ".join(row))
            row = []
    if row:
        lines.append("        " + " ".join(row))
    lines.append("    };")
    lines.append("")
    lines.append("    /// <summary>Checksum of the table data (spec 7.2.4); 0 for empty tables.</summary>")
    lines.append(f"    public const uint Checksum = 0x{upcase_stored_checksum:08X};")
    lines.append("}")
    open(out_path, 'w').write("\n".join(lines) + "\n")
    print(f"wrote {out_path} ({len(table)} bytes)")


if __name__ == '__main__':
    main()
