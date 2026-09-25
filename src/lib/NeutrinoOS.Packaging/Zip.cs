// NeutrinoOS Phase 8 - shared packaging library (NeutrinoOS.Packaging).
//
// Zip: ZIP archive reader (stored + deflate entries, central directory
// driven, no ZIP64) and writer (stored entries, deterministic output).
// .npkg packages are ZIP archives, so this is the container layer used by
// NpkgPackage on both the device (Tier-0 JIT / korlib subset) and the host.

using System;
using System.Collections.Generic;
using System.Text;

namespace NeutrinoOS.Packaging
{
    /// <summary>
    /// Phase 8: one entry of a .npkg ZIP archive, populated from the central
    /// directory (sizes/CRC are therefore always the authoritative values).
    /// </summary>
    public sealed class ZipEntry
    {
        /// <summary>Phase 8: entry name (forward-slash separated, UTF-8 decoded).</summary>
        public string Name { get; set; }

        /// <summary>Phase 8: compression method (0 = stored, 8 = deflate).</summary>
        public int Method { get; set; }

        /// <summary>Phase 8: size of the compressed data in the archive.</summary>
        public long CompressedSize { get; set; }

        /// <summary>Phase 8: size of the original (decompressed) data.</summary>
        public long UncompressedSize { get; set; }

        /// <summary>Phase 8: CRC-32 of the original data (verified on Read).</summary>
        public uint Crc32 { get; set; }

        /// <summary>Phase 8: true when the name ends with '/' (directory entry).</summary>
        public bool IsDirectory { get; set; }

        /// <summary>Phase 8: offset of the local file header inside the archive.</summary>
        public long LocalHeaderOffset { get; set; }

        /// <summary>Phase 8: general purpose bit flags from the central directory.</summary>
        public int Flags { get; set; }
    }

    /// <summary>
    /// Phase 8: reads .npkg ZIP archives (up to 4 GB, no ZIP64) from a byte
    /// array. Parses the central directory up front so entry metadata is
    /// available without scanning the file data.
    /// </summary>
    public sealed class ZipReader
    {
        private readonly byte[] _data;
        private readonly ZipEntry[] _entries;

        /// <summary>
        /// Phase 8: parses the central directory of a ZIP archive held in
        /// memory. Throws FormatException for malformed input, ZIP64
        /// archives and multi-disk archives.
        /// </summary>
        public ZipReader(byte[] archive)
        {
            if (archive == null)
                throw new ArgumentNullException("archive");
            _data = archive;
            _entries = ParseCentralDirectory();
        }

        /// <summary>
        /// Phase 8: all entries in central directory order (a copy;
        /// mutating it does not affect the reader).
        /// </summary>
        public ZipEntry[] Entries()
        {
            ZipEntry[] copy = new ZipEntry[_entries.Length];
            for (int i = 0; i < _entries.Length; i++)
                copy[i] = _entries[i];
            return copy;
        }

        /// <summary>
        /// Phase 8: finds an entry by exact name; when the name does not end
        /// with '/', "name/" is additionally tried so directories can be
        /// looked up without the trailing slash. Returns null when absent.
        /// </summary>
        public ZipEntry Find(string name)
        {
            if (name == null)
                return null;
            for (int i = 0; i < _entries.Length; i++)
            {
                if (OrdinalEquals(_entries[i].Name, name))
                    return _entries[i];
            }
            if (name.Length > 0 && name[name.Length - 1] != '/')
            {
                string directoryName = name + "/";
                for (int i = 0; i < _entries.Length; i++)
                {
                    if (OrdinalEquals(_entries[i].Name, directoryName))
                        return _entries[i];
                }
            }
            return null;
        }

        /// <summary>
        /// Phase 8: extracts an entry: method 0 slices the archive, method 8
        /// runs Inflate.Raw; the CRC-32 is always verified and unsupported
        /// methods throw FormatException.
        /// </summary>
        public byte[] Read(ZipEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException("entry");

            long headerOffset = entry.LocalHeaderOffset;
            if (headerOffset < 0 || headerOffset + 30 > _data.Length)
                throw new FormatException("zip: local file header offset is out of bounds");
            int at = (int)headerOffset;
            if (ReadU32(_data, at) != 0x04034B50u)
                throw new FormatException("zip: bad local file header signature for '" + entry.Name + "'");

            int nameLength = ReadU16(_data, at + 26);
            int extraLength = ReadU16(_data, at + 28);
            long dataOffset = headerOffset + 30 + nameLength + extraLength;
            if (entry.CompressedSize < 0 || entry.CompressedSize > 0x7FFFFFF0L)
                throw new FormatException("zip: compressed size is out of range for '" + entry.Name + "'");
            if (dataOffset < 0 || dataOffset > _data.Length - entry.CompressedSize)
                throw new FormatException("zip: file data is out of bounds for '" + entry.Name + "'");

            if (entry.Method == 0)
            {
                if (entry.CompressedSize != entry.UncompressedSize)
                    throw new FormatException("zip: stored entry has mismatched sizes for '" + entry.Name + "'");
                byte[] raw = CopyRange(_data, (int)dataOffset, (int)entry.CompressedSize);
                VerifyCrc(entry, raw);
                return raw;
            }
            if (entry.Method == 8)
            {
                byte[] raw;
                if (entry.CompressedSize == 0 && entry.UncompressedSize == 0)
                {
                    // Degenerate but harmless: some writers emit an empty data
                    // stream for a zero-length file (a real DEFLATE stream
                    // always has at least one block).
                    raw = new byte[0];
                }
                else
                {
                    raw = Inflate.Raw(_data, (int)dataOffset, (int)entry.CompressedSize, entry.UncompressedSize);
                }
                VerifyCrc(entry, raw);
                return raw;
            }
            throw new FormatException("unsupported compression method " + TextConv.LongToString((long)entry.Method));
        }

        private ZipEntry[] ParseCentralDirectory()
        {
            int searchStart = _data.Length - 22;
            int searchEnd = _data.Length - 22 - 65535;
            if (searchEnd < 0)
                searchEnd = 0;

            int eocd = -1;
            for (int i = searchStart; i >= searchEnd; i--)
            {
                if (ReadU32(_data, i) == 0x06054B50u)
                {
                    eocd = i;
                    break;
                }
            }
            if (eocd < 0)
                throw new FormatException("zip: end of central directory record not found (not a ZIP archive?)");

            // A ZIP64 archive always carries the ZIP64 EOCD locator directly
            // before the classic EOCD record.
            if (eocd >= 20 && ReadU32(_data, eocd - 20) == 0x07064B50u)
                throw new FormatException("zip: ZIP64 archives are not supported (ZIP64 end of central directory record present)");

            int diskNumber = ReadU16(_data, eocd + 4);
            int centralDiskNumber = ReadU16(_data, eocd + 6);
            int entriesOnDisk = ReadU16(_data, eocd + 8);
            int totalEntries = ReadU16(_data, eocd + 10);
            long centralSize = ReadU32(_data, eocd + 12);
            long centralOffset = ReadU32(_data, eocd + 16);

            if (totalEntries == 0xFFFF || centralSize == 0xFFFFFFFFL || centralOffset == 0xFFFFFFFFL)
                throw new FormatException("zip: ZIP64 archives are not supported (32-bit fields are saturated)");
            if (diskNumber != 0 || centralDiskNumber != 0 || entriesOnDisk != totalEntries)
                throw new FormatException("zip: multi-disk archives are not supported");
            if (centralOffset < 0 || centralOffset > _data.Length || centralSize > _data.Length - centralOffset)
                throw new FormatException("zip: central directory is out of bounds");

            ZipEntry[] entries = new ZipEntry[totalEntries];
            long cursor = centralOffset;
            for (int i = 0; i < totalEntries; i++)
            {
                if (cursor < 0 || cursor + 46 > _data.Length)
                    throw new FormatException("zip: central directory entry is out of bounds");
                int at = (int)cursor;
                if (ReadU32(_data, at) != 0x02014B50u)
                    throw new FormatException("zip: bad central directory entry signature");

                int flags = ReadU16(_data, at + 8);
                int method = ReadU16(_data, at + 10);
                uint crc = (uint)ReadU32(_data, at + 16);
                long compressedSize = ReadU32(_data, at + 20);
                long uncompressedSize = ReadU32(_data, at + 24);
                int nameLength = ReadU16(_data, at + 28);
                int extraLength = ReadU16(_data, at + 30);
                int commentLength = ReadU16(_data, at + 32);
                long localOffset = ReadU32(_data, at + 42);

                if (compressedSize == 0xFFFFFFFFL || uncompressedSize == 0xFFFFFFFFL || localOffset == 0xFFFFFFFFL)
                    throw new FormatException("zip: ZIP64 archives are not supported (ZIP64 entry fields present)");
                if (cursor + 46 + nameLength + extraLength + commentLength > _data.Length)
                    throw new FormatException("zip: central directory entry extends past the end of the archive");

                ZipEntry entry = new ZipEntry();
                entry.Name = Encoding.UTF8.GetString(_data, at + 46, nameLength);
                entry.Flags = flags;
                entry.Method = method;
                entry.Crc32 = crc;
                entry.CompressedSize = compressedSize;
                entry.UncompressedSize = uncompressedSize;
                entry.LocalHeaderOffset = localOffset;
                entry.IsDirectory = entry.Name.Length > 0 && entry.Name[entry.Name.Length - 1] == '/';
                entries[i] = entry;

                cursor += 46 + nameLength + extraLength + commentLength;
            }
            return entries;
        }

        private static void VerifyCrc(ZipEntry entry, byte[] data)
        {
            uint actual = TextConv.Crc32(data, 0, data.Length);
            if (actual != entry.Crc32)
                throw new FormatException("zip: CRC-32 mismatch for '" + entry.Name + "' (expected "
                    + TextConv.LongToString((long)entry.Crc32) + ", got " + TextConv.LongToString((long)actual) + ")");
        }

        private static byte[] CopyRange(byte[] data, int offset, int count)
        {
            byte[] result = new byte[count];
            for (int i = 0; i < count; i++)
                result[i] = data[offset + i];
            return result;
        }

        private static bool OrdinalEquals(string a, string b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static int ReadU16(byte[] data, int offset)
        {
            return (data[offset] & 0xFF) | ((data[offset + 1] & 0xFF) << 8);
        }

        private static long ReadU32(byte[] data, int offset)
        {
            return (long)(data[offset] & 0xFF)
                | ((long)(data[offset + 1] & 0xFF) << 8)
                | ((long)(data[offset + 2] & 0xFF) << 16)
                | ((long)(data[offset + 3] & 0xFF) << 24);
        }
    }

    /// <summary>
    /// Phase 8: builds .npkg ZIP archives with stored (uncompressed) entries
    /// and a fixed DOS timestamp of 1980-01-01, so identical input always
    /// produces byte-identical archives (required for reproducible Ed25519
    /// signatures).
    /// </summary>
    public sealed class ZipWriter
    {
        private readonly List<string> _names = new List<string>();
        private readonly List<byte[]> _datas = new List<byte[]>();
        private readonly List<bool> _directories = new List<bool>();

        /// <summary>Phase 8: appends a stored file entry (name, then content bytes).</summary>
        public void AddFile(string name, byte[] data)
        {
            if (name == null || name.Length == 0)
                throw new ArgumentException("zip: entry name must not be empty");
            if (data == null)
                throw new ArgumentNullException("data");
            _names.Add(name);
            _datas.Add(data);
            _directories.Add(name[name.Length - 1] == '/');
        }

        /// <summary>
        /// Phase 8: appends a directory entry; the trailing '/' is added
        /// automatically when missing.
        /// </summary>
        public void AddDirectory(string name)
        {
            if (name == null || name.Length == 0)
                throw new ArgumentException("zip: entry name must not be empty");
            if (name[name.Length - 1] != '/')
                name = name + "/";
            AddFile(name, new byte[0]);
        }

        /// <summary>
        /// Phase 8: serializes the archive: local headers + data, central
        /// directory and end-of-central-directory record, with the UTF-8
        /// name flag (bit 11) set. Throws InvalidOperationException when the
        /// output would not fit the 32-bit non-ZIP64 format.
        /// </summary>
        public byte[] Finish()
        {
            int count = _names.Count;
            if (count > 0xFFFF)
                throw new InvalidOperationException("zip: too many entries for a non-ZIP64 archive");

            byte[][] nameBytes = new byte[count][];
            uint[] crcs = new uint[count];
            long[] sizes = new long[count];
            long localSize = 0;
            long centralSize = 0;
            for (int i = 0; i < count; i++)
            {
                byte[] nb = Encoding.UTF8.GetBytes(_names[i]);
                if (nb.Length > 0xFFFF)
                    throw new InvalidOperationException("zip: entry name is too long");
                nameBytes[i] = nb;
                sizes[i] = (long)_datas[i].Length;
                crcs[i] = TextConv.Crc32(_datas[i], 0, _datas[i].Length);
                localSize += 30 + nb.Length + sizes[i];
                centralSize += 46 + nb.Length;
            }

            long total = localSize + centralSize + 22;
            if (total > 0x7FFFFFF0L)
                throw new InvalidOperationException("zip: output archive exceeds the non-ZIP64 size limit");

            byte[] output = new byte[(int)total];
            int p = 0;
            long[] offsets = new long[count];

            for (int i = 0; i < count; i++)
            {
                offsets[i] = p;
                WriteU32(output, p, 0x04034B50u); p += 4;   // local file header
                WriteU16(output, p, 20); p += 2;            // version needed (2.0)
                WriteU16(output, p, 0x0800); p += 2;        // UTF-8 name flag
                WriteU16(output, p, 0); p += 2;             // stored
                WriteU16(output, p, 0); p += 2;             // DOS time 00:00:00
                WriteU16(output, p, 0x0021); p += 2;        // DOS date 1980-01-01
                WriteU32(output, p, (long)crcs[i]); p += 4;
                WriteU32(output, p, sizes[i]); p += 4;
                WriteU32(output, p, sizes[i]); p += 4;
                WriteU16(output, p, nameBytes[i].Length); p += 2;
                WriteU16(output, p, 0); p += 2;             // extra length
                p = CopyBytes(output, p, nameBytes[i]);
                p = CopyBytes(output, p, _datas[i]);
            }

            long centralStart = p;
            for (int i = 0; i < count; i++)
            {
                WriteU32(output, p, 0x02014B50u); p += 4;   // central directory header
                WriteU16(output, p, 20); p += 2;            // version made by (2.0)
                WriteU16(output, p, 20); p += 2;            // version needed (2.0)
                WriteU16(output, p, 0x0800); p += 2;        // UTF-8 name flag
                WriteU16(output, p, 0); p += 2;             // stored
                WriteU16(output, p, 0); p += 2;             // DOS time
                WriteU16(output, p, 0x0021); p += 2;        // DOS date
                WriteU32(output, p, (long)crcs[i]); p += 4;
                WriteU32(output, p, sizes[i]); p += 4;
                WriteU32(output, p, sizes[i]); p += 4;
                WriteU16(output, p, nameBytes[i].Length); p += 2;
                WriteU16(output, p, 0); p += 2;             // extra length
                WriteU16(output, p, 0); p += 2;             // comment length
                WriteU16(output, p, 0); p += 2;             // disk number start
                WriteU16(output, p, 0); p += 2;             // internal attributes
                WriteU32(output, p, _directories[i] ? 0x10L : 0L); p += 4; // external attributes
                WriteU32(output, p, offsets[i]); p += 4;
                p = CopyBytes(output, p, nameBytes[i]);
            }

            long centralEnd = p;
            WriteU32(output, p, 0x06054B50u); p += 4;       // end of central directory
            WriteU16(output, p, 0); p += 2;                 // this disk
            WriteU16(output, p, 0); p += 2;                 // central directory disk
            WriteU16(output, p, count); p += 2;             // entries on this disk
            WriteU16(output, p, count); p += 2;             // total entries
            WriteU32(output, p, centralEnd - centralStart); p += 4;
            WriteU32(output, p, centralStart); p += 4;
            WriteU16(output, p, 0); p += 2;                 // comment length

            if (p != output.Length)
                throw new InvalidOperationException("zip: internal error (size accounting mismatch)");
            return output;
        }

        private static int CopyBytes(byte[] destination, int offset, byte[] source)
        {
            for (int i = 0; i < source.Length; i++)
                destination[offset + i] = source[i];
            return offset + source.Length;
        }

        private static void WriteU16(byte[] destination, int offset, int value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteU32(byte[] destination, int offset, long value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }
    }
}
