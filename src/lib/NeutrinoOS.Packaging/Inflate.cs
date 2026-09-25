// NeutrinoOS Phase 8 - shared packaging library (NeutrinoOS.Packaging).
//
// Inflate: a complete raw-DEFLATE (RFC 1951) decompressor used by the .npkg
// ZIP reader.  Hand-written because the korlib subset has no
// System.IO.Compression.  Runs identically under the Tier-0 JIT and desktop
// .NET: only byte arrays, ints and manual loops.

using System;

namespace NeutrinoOS.Packaging
{
    /// <summary>
    /// Phase 8: raw DEFLATE decompressor (no zlib/gzip wrapper). Used for
    /// method-8 ZIP entries inside .npkg packages. Supports stored, fixed
    /// Huffman and dynamic Huffman blocks, multiple blocks per stream, and
    /// both "known output size" (preallocated, exact match required) and
    /// "grow as needed" modes. Corrupt input throws FormatException.
    /// </summary>
    public static class Inflate
    {
        private static readonly int[] LengthBase = new int[]
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
            35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
        };

        private static readonly int[] LengthExtra = new int[]
        {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
            3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
        };

        private static readonly int[] DistBase = new int[]
        {
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
            257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577
        };

        private static readonly int[] DistExtra = new int[]
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
            7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13
        };

        private static readonly int[] CodeLengthOrder = new int[]
        {
            16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15
        };

        /// <summary>
        /// Phase 8: decompresses a raw DEFLATE stream. When expectedSize is
        /// &gt;= 0 the output buffer is preallocated exactly and any size
        /// mismatch (or overflow) throws FormatException; when it is negative
        /// the output grows on demand. Returns the decompressed bytes.
        /// </summary>
        public static byte[] Raw(byte[] data, int offset, int count, long expectedSize)
        {
            if (data == null)
                throw new ArgumentNullException("data");
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException("offset");
            if (expectedSize > 0x7FFFFFF0L)
                throw new FormatException("inflate: expected output size is too large");

            bool exact = expectedSize >= 0;
            int capacity = exact ? (int)expectedSize : count * 4 + 64;
            byte[] buffer = new byte[capacity];
            int length = 0;

            BitReader reader = new BitReader(data, offset, offset + count);
            while (true)
            {
                int final = reader.ReadBits(1);
                int type = reader.ReadBits(2);

                if (type == 0)
                {
                    reader.AlignToByte();
                    int storedLength = reader.ReadByteAligned() | (reader.ReadByteAligned() << 8);
                    int storedComplement = reader.ReadByteAligned() | (reader.ReadByteAligned() << 8);
                    if ((storedLength ^ 0xFFFF) != storedComplement)
                        throw new FormatException("inflate: stored block length check failed");
                    for (int i = 0; i < storedLength; i++)
                        Push(ref buffer, ref length, (byte)reader.ReadByteAligned(), exact);
                }
                else if (type == 1)
                {
                    Huffman literal = BuildFixedLiteralTree();
                    Huffman distance = BuildFixedDistanceTree();
                    DecodeBlock(reader, literal, distance, ref buffer, ref length, exact);
                }
                else if (type == 2)
                {
                    Huffman literal;
                    Huffman distance;
                    BuildDynamicTrees(reader, out literal, out distance);
                    DecodeBlock(reader, literal, distance, ref buffer, ref length, exact);
                }
                else
                {
                    throw new FormatException("inflate: invalid block type");
                }

                if (final == 1)
                    break;
            }

            if (exact && length != (int)expectedSize)
                throw new FormatException("inflate: decompressed size mismatch (expected "
                    + TextConv.LongToString(expectedSize) + ", got " + TextConv.LongToString((long)length) + ")");

            if (exact)
                return buffer;

            byte[] result = new byte[length];
            for (int i = 0; i < length; i++)
                result[i] = buffer[i];
            return result;
        }

        private static void DecodeBlock(BitReader reader, Huffman literal, Huffman distance,
            ref byte[] buffer, ref int length, bool exact)
        {
            while (true)
            {
                int symbol = literal.Decode(reader);
                if (symbol < 256)
                {
                    Push(ref buffer, ref length, (byte)symbol, exact);
                }
                else if (symbol == 256)
                {
                    return; // end of block
                }
                else
                {
                    int lengthCode = symbol - 257;
                    if (lengthCode >= 29)
                        throw new FormatException("inflate: invalid length code");
                    int copyLength = LengthBase[lengthCode] + reader.ReadBits(LengthExtra[lengthCode]);

                    int distanceSymbol = distance.Decode(reader);
                    if (distanceSymbol >= 30)
                        throw new FormatException("inflate: invalid distance code");
                    int copyDistance = DistBase[distanceSymbol] + reader.ReadBits(DistExtra[distanceSymbol]);

                    if (copyDistance > length)
                        throw new FormatException("inflate: distance exceeds the output produced so far");
                    for (int i = 0; i < copyLength; i++)
                        Push(ref buffer, ref length, buffer[length - copyDistance], exact);
                }
            }
        }

        private static void BuildDynamicTrees(BitReader reader, out Huffman literal, out Huffman distance)
        {
            int literalCount = reader.ReadBits(5) + 257;
            int distanceCount = reader.ReadBits(5) + 1;
            int codeLengthCount = reader.ReadBits(4) + 4;

            int[] codeLengthLengths = new int[19];
            for (int i = 0; i < codeLengthCount; i++)
                codeLengthLengths[CodeLengthOrder[i]] = reader.ReadBits(3);
            Huffman codeLengthTree = new Huffman(codeLengthLengths, 19);

            int total = literalCount + distanceCount;
            int[] lengths = new int[total];
            int at = 0;
            while (at < total)
            {
                int symbol = codeLengthTree.Decode(reader);
                if (symbol < 16)
                {
                    lengths[at] = symbol;
                    at++;
                }
                else if (symbol == 16)
                {
                    if (at == 0)
                        throw new FormatException("inflate: code length repeat with no previous length");
                    int previous = lengths[at - 1];
                    int repeat = 3 + reader.ReadBits(2);
                    if (at + repeat > total)
                        throw new FormatException("inflate: code length repeat overflows the table");
                    for (int i = 0; i < repeat; i++)
                    {
                        lengths[at] = previous;
                        at++;
                    }
                }
                else if (symbol == 17 || symbol == 18)
                {
                    int repeat = symbol == 17 ? 3 + reader.ReadBits(3) : 11 + reader.ReadBits(7);
                    if (at + repeat > total)
                        throw new FormatException("inflate: code length repeat overflows the table");
                    for (int i = 0; i < repeat; i++)
                    {
                        lengths[at] = 0;
                        at++;
                    }
                }
                else
                {
                    throw new FormatException("inflate: invalid code length symbol");
                }
            }

            int[] literalLengths = new int[literalCount];
            for (int i = 0; i < literalCount; i++)
                literalLengths[i] = lengths[i];
            int[] distanceLengths = new int[distanceCount];
            for (int i = 0; i < distanceCount; i++)
                distanceLengths[i] = lengths[literalCount + i];

            literal = new Huffman(literalLengths, literalCount);
            distance = new Huffman(distanceLengths, distanceCount);
        }

        private static Huffman BuildFixedLiteralTree()
        {
            int[] lengths = new int[288];
            int i = 0;
            for (; i < 144; i++)
                lengths[i] = 8;
            for (; i < 256; i++)
                lengths[i] = 9;
            for (; i < 280; i++)
                lengths[i] = 7;
            for (; i < 288; i++)
                lengths[i] = 8;
            return new Huffman(lengths, 288);
        }

        private static Huffman BuildFixedDistanceTree()
        {
            int[] lengths = new int[32];
            for (int i = 0; i < 32; i++)
                lengths[i] = 5;
            return new Huffman(lengths, 32);
        }

        private static void Push(ref byte[] buffer, ref int length, byte value, bool exact)
        {
            if (length >= buffer.Length)
            {
                if (exact)
                    throw new FormatException("inflate: output exceeds the expected size");
                int capacity = buffer.Length * 2;
                if (capacity <= 0)
                    capacity = 1024;
                byte[] bigger = new byte[capacity];
                for (int i = 0; i < length; i++)
                    bigger[i] = buffer[i];
                buffer = bigger;
            }
            buffer[length] = value;
            length++;
        }

        /// <summary>
        /// Phase 8: LSB-first bit reader over a byte range (DEFLATE bit order).
        /// </summary>
        private sealed class BitReader
        {
            private readonly byte[] _data;
            private readonly int _end;
            private int _pos;
            private int _bitBuffer;
            private int _bitCount;

            public BitReader(byte[] data, int start, int end)
            {
                _data = data;
                _end = end;
                _pos = start;
                _bitBuffer = 0;
                _bitCount = 0;
            }

            public int ReadBits(int n)
            {
                while (_bitCount < n)
                {
                    if (_pos >= _end)
                        throw new FormatException("inflate: unexpected end of stream");
                    _bitBuffer |= (_data[_pos] & 0xFF) << _bitCount;
                    _pos++;
                    _bitCount += 8;
                }
                int value = _bitBuffer & ((1 << n) - 1);
                _bitBuffer >>= n;
                _bitCount -= n;
                return value;
            }

            /// <summary>Discards the remaining bits of the current byte.</summary>
            public void AlignToByte()
            {
                int discard = _bitCount & 7;
                if (discard != 0)
                {
                    _bitBuffer >>= discard;
                    _bitCount -= discard;
                }
            }

            public int ReadByteAligned()
            {
                if (_bitCount >= 8)
                {
                    int pending = _bitBuffer & 0xFF;
                    _bitBuffer >>= 8;
                    _bitCount -= 8;
                    return pending;
                }
                if (_pos >= _end)
                    throw new FormatException("inflate: unexpected end of stream");
                int value = _data[_pos] & 0xFF;
                _pos++;
                return value;
            }
        }

        /// <summary>
        /// Phase 8: canonical Huffman decoder (length counts + symbols sorted
        /// by code length) with the standard incremental bit-by-bit decode.
        /// </summary>
        private sealed class Huffman
        {
            private readonly int[] _counts;
            private readonly int[] _symbols;

            public Huffman(int[] lengths, int count)
            {
                _counts = new int[16];
                for (int i = 0; i < count; i++)
                {
                    int len = lengths[i];
                    if (len < 0 || len > 15)
                        throw new FormatException("inflate: invalid code length");
                    if (len > 0)
                        _counts[len]++;
                }

                int left = 1;
                for (int len = 1; len <= 15; len++)
                {
                    left <<= 1;
                    left -= _counts[len];
                    if (left < 0)
                        throw new FormatException("inflate: over-subscribed Huffman code");
                }

                int total = 0;
                int[] offsets = new int[16];
                for (int len = 1; len <= 15; len++)
                {
                    offsets[len] = total;
                    total += _counts[len];
                }

                _symbols = new int[total];
                for (int i = 0; i < count; i++)
                {
                    int len = lengths[i];
                    if (len > 0)
                    {
                        _symbols[offsets[len]] = i;
                        offsets[len]++;
                    }
                }
            }

            public int Decode(BitReader reader)
            {
                int code = 0;
                int first = 0;
                int index = 0;
                for (int len = 1; len <= 15; len++)
                {
                    code |= reader.ReadBits(1);
                    int count = _counts[len];
                    if (code - count < first)
                        return _symbols[index + (code - first)];
                    index += count;
                    first += count;
                    first <<= 1;
                    code <<= 1;
                }
                throw new FormatException("inflate: invalid Huffman code");
            }
        }
    }
}
