// NeutrinoOS DDK - HPACK header compression (RFC 7541) - Phase 9 Task 3
//
// Encoder side (server responses): static-table lookups, literals with
// and without name references, Huffman string encoding, integer
// primitives. Decoder side (client requests): full support for indexed
// fields, dynamic table size updates, incremental indexing (client
// tables grow!), and Huffman strings.
//
// Style note: the DDK is JIT-compiled by the Tier-0 JIT - keep to plain
// arrays/loops, avoid fancy BCL surface, and never take addresses of
// fields of class instances.

using System;

namespace NeutrinoOS.DDK.Services;

/// <summary>HPACK integer/string primitives + static table lookup.</summary>
public static class Hpack
{
    /// <summary>HPACK static table entry count (RFC 7541 appendix A).</summary>
    public const int StaticCount = 61;

    /// <summary>Per-entry overhead for the dynamic table (RFC 7541 4.1).</summary>
    public const int EntryOverhead = 32;

    /// <summary>Look up a static entry's name; null when out of range.</summary>
    public static string StaticName(int index)
    {
        if (index < 1 || index > StaticCount)
            return null;
        return HpackTables.StaticNames[index - 1];
    }

    /// <summary>Look up a static entry's value; null when out of range.</summary>
    public static string StaticValue(int index)
    {
        if (index < 1 || index > StaticCount)
            return null;
        return HpackTables.StaticValues[index - 1];
    }

    /// <summary>Find an exact name+value match in the static table (1-based), or 0.</summary>
    public static int FindStatic(string name, string value)
    {
        for (int i = 0; i < StaticCount; i++)
        {
            if (StrEq(HpackTables.StaticNames[i], name) &&
                StrEq(HpackTables.StaticValues[i], value))
                return i + 1;
        }
        return 0;
    }

    /// <summary>Find a name-only match in the static table (1-based), or 0.</summary>
    public static int FindStaticName(string name)
    {
        for (int i = 0; i < StaticCount; i++)
        {
            if (StrEq(HpackTables.StaticNames[i], name))
                return i + 1;
        }
        return 0;
    }

    /// <summary>Case-insensitive ASCII equality.</summary>
    public static bool StrEq(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i];
            char cb = b[i];
            if (ca >= 'A' && ca <= 'Z')
                ca = (char)(ca + 32);
            if (cb >= 'A' && cb <= 'Z')
                cb = (char)(cb + 32);
            if (ca != cb)
                return false;
        }
        return true;
    }

    /// <summary>Write an HPACK integer with a prefix of <paramref name="prefixBits"/>.</summary>
    public static int WriteInt(byte[] buf, int pos, int value, int prefixBits, byte pattern)
    {
        int maxPrefix = (1 << prefixBits) - 1;
        if (value < maxPrefix)
        {
            buf[pos++] = (byte)(pattern | value);
            return pos;
        }
        buf[pos++] = (byte)(pattern | maxPrefix);
        value -= maxPrefix;
        while (value >= 128)
        {
            buf[pos++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        buf[pos++] = (byte)value;
        return pos;
    }

    /// <summary>Write a (possibly Huffman-encoded) HPACK string.</summary>
    public static int WriteString(byte[] buf, int pos, string s)
    {
        if (s == null)
            s = "";
        // Encode to bytes first (ASCII/UTF-8 for our header values).
        var raw = new byte[s.Length];
        int rawLen = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x80)
                raw[rawLen++] = (byte)c;
            else if (c < 0x800)
            {
                raw[rawLen++] = (byte)(0xC0 | (c >> 6));
                raw[rawLen++] = (byte)(0x80 | (c & 0x3F));
            }
            else
            {
                raw[rawLen++] = (byte)(0xE0 | (c >> 12));
                raw[rawLen++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                raw[rawLen++] = (byte)(0x80 | (c & 0x3F));
            }
        }

        // Huffman-encode; keep whichever is smaller (RFC: either is valid).
        var huf = new byte[rawLen * 2 + 2];
        int hufLen = HuffmanEncode(raw, rawLen, huf);

        if (hufLen < rawLen)
        {
            pos = WriteInt(buf, pos, hufLen, 7, 0x80);
            for (int i = 0; i < hufLen; i++)
                buf[pos++] = huf[i];
        }
        else
        {
            pos = WriteInt(buf, pos, rawLen, 7, 0x00);
            for (int i = 0; i < rawLen; i++)
                buf[pos++] = raw[i];
        }
        return pos;
    }

    /// <summary>Huffman-encode bytes (RFC 7541 appendix B); returns output length.</summary>
    public static int HuffmanEncode(byte[] input, int inputLen, byte[] output)
    {
        ulong bits = 0;
        int bitCount = 0;
        int outPos = 0;
        for (int i = 0; i < inputLen; i++)
        {
            uint code = HpackTables.HuffmanCodes[input[i]];
            int len = HpackTables.HuffmanLengths[input[i]];
            bits = (bits << len) | code;
            bitCount += len;
            while (bitCount >= 8)
            {
                bitCount -= 8;
                output[outPos++] = (byte)(bits >> bitCount);
            }
        }
        if (bitCount > 0)
        {
            // Pad with EOS's 1-bits (i.e. 1s), per RFC 7541 5.2.
            int pad = 8 - bitCount;
            bits = (bits << pad) | (uint)((1 << pad) - 1);
            output[outPos++] = (byte)bits;
        }
        return outPos;
    }
}

/// <summary>HPACK decoding side for one HTTP/2 connection.</summary>
public sealed class HpackDecoder
{
    private readonly string[] _dynNames = new string[256];
    private readonly string[] _dynValues = new string[256];
    private int _dynCount;
    private int _dynSize;
    private int _dynMax = 4096;

    /// <summary>Set the maximum dynamic table size (SETTINGS_HEADER_TABLE_SIZE).</summary>
    public void SetMaxSize(int size)
    {
        if (size < 0)
            size = 0;
        _dynMax = size;
        Evict();
    }

    private void Evict()
    {
        while (_dynSize > _dynMax && _dynCount > 0)
        {
            // Entries are stored newest-first; drop the oldest.
            int last = _dynCount - 1;
            _dynSize -= Hpack.EntryOverhead + _dynNames[last].Length + _dynValues[last].Length;
            _dynNames[last] = null;
            _dynValues[last] = null;
            _dynCount--;
        }
    }

    private void Insert(string name, string value)
    {
        if (_dynCount >= _dynNames.Length)
        {
            // Table smaller than a single large entry; drop everything.
            _dynCount = 0;
            _dynSize = 0;
            return;
        }
        for (int i = _dynCount; i > 0; i--)
        {
            _dynNames[i] = _dynNames[i - 1];
            _dynValues[i] = _dynValues[i - 1];
        }
        _dynNames[0] = name;
        _dynValues[0] = value;
        _dynCount++;
        _dynSize += Hpack.EntryOverhead + name.Length + value.Length;
        Evict();
    }

    private string LookupName(int index, out bool found)
    {
        found = true;
        if (index >= 1 && index <= Hpack.StaticCount)
            return Hpack.StaticName(index);
        int dyn = index - Hpack.StaticCount - 1;
        if (dyn >= 0 && dyn < _dynCount)
            return _dynNames[dyn];
        found = false;
        return null;
    }

    private string LookupValue(int index, out bool found)
    {
        found = true;
        if (index >= 1 && index <= Hpack.StaticCount)
            return Hpack.StaticValue(index);
        int dyn = index - Hpack.StaticCount - 1;
        if (dyn >= 0 && dyn < _dynCount)
            return _dynValues[dyn];
        found = false;
        return null;
    }

    /// <summary>
    /// Decode a header block into name/value pairs. Returns the number of
    /// pairs, or -1 on a decoding error.
    /// </summary>
    public int Decode(byte[] block, int length, string[] names, string[] values)
    {
        int pos = 0;
        int count = 0;
        while (pos < length)
        {
            if (count >= names.Length)
                return -1;
            byte b = block[pos];
            if ((b & 0x80) != 0)
            {
                // Indexed header field.
                int index = ReadInt(block, ref pos, 7);
                if (index == 0)
                    return -1;
                bool found;
                string v = LookupValue(index, out found);
                if (!found)
                    return -1;
                string n = LookupName(index, out found);
                if (!found)
                    return -1;
                names[count] = n;
                values[count] = v;
                count++;
            }
            else if ((b & 0xC0) == 0x40)
            {
                // Literal with incremental indexing.
                int index = ReadInt(block, ref pos, 6);
                string name;
                if (index == 0)
                {
                    name = ReadString(block, ref pos);
                    if (name == null)
                        return -1;
                }
                else
                {
                    bool found;
                    name = LookupName(index, out found);
                    if (!found)
                        return -1;
                }
                string value = ReadString(block, ref pos);
                if (value == null)
                    return -1;
                names[count] = name;
                values[count] = value;
                count++;
                Insert(name, value);
            }
            else if ((b & 0xE0) == 0x20)
            {
                // Dynamic table size update.
                int size = ReadInt(block, ref pos, 5);
                if (size > _dynMax)
                    return -1;
                _dynMax = size;
                Evict();
            }
            else
            {
                // Literal without indexing / never indexed.
                int index = ReadInt(block, ref pos, 4);
                string name;
                if (index == 0)
                {
                    name = ReadString(block, ref pos);
                    if (name == null)
                        return -1;
                }
                else
                {
                    bool found;
                    name = LookupName(index, out found);
                    if (!found)
                        return -1;
                }
                string value = ReadString(block, ref pos);
                if (value == null)
                    return -1;
                names[count] = name;
                values[count] = value;
                count++;
            }
        }
        return count;
    }

    /// <summary>Read an HPACK integer with the given prefix width.</summary>
    public static int ReadInt(byte[] buf, ref int pos, int prefixBits)
    {
        int maxPrefix = (1 << prefixBits) - 1;
        int value = buf[pos++] & maxPrefix;
        if (value < maxPrefix)
            return value;
        int shift = 0;
        while (true)
        {
            if (pos >= buf.Length)
                return -1;
            byte b = buf[pos++];
            value += (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
            if (shift > 28)
                return -1;
        }
        return value;
    }

    /// <summary>Read a raw or Huffman-coded HPACK string; null on error.</summary>
    public static string ReadString(byte[] buf, ref int pos)
    {
        if (pos >= buf.Length)
            return null;
        bool huffman = (buf[pos] & 0x80) != 0;
        int len = ReadInt(buf, ref pos, 7);
        if (len < 0 || pos + len > buf.Length)
            return null;
        string s;
        if (huffman)
        {
            s = HuffmanDecode(buf, pos, len);
            if (s == null)
                return null;
        }
        else
        {
            var chars = new char[len];
            int n = 0;
            for (int i = 0; i < len; i++)
            {
                byte b = buf[pos + i];
                if (b < 0x80)
                    chars[n++] = (char)b;
                else
                {
                    // Minimal UTF-8 decode (header values are effectively ASCII).
                    chars[n++] = (char)b;
                }
            }
            s = new string(chars, 0, n);
        }
        pos += len;
        return s;
    }

    /// <summary>Decode a Huffman string (RFC 7541 appendix B).</summary>
    public static string HuffmanDecode(byte[] buf, int pos, int length)
    {
        BuildHuffmanTree();
        var chars = new char[length * 2 + 2];
        int n = 0;
        int node = 0;
        int bitsSinceSymbol = 0;
        bool tailAllOnes = true;
        for (int i = 0; i < length; i++)
        {
            byte b = buf[pos + i];
            for (int bit = 7; bit >= 0; bit--)
            {
                int bitVal = (b >> bit) & 1;
                node = bitVal != 0 ? _huffRight[node] : _huffLeft[node];
                if (node < 0)
                    return null;
                int sym = _huffSym[node];
                if (sym >= 0)
                {
                    if (sym == 256)
                        return null;   // EOS must not appear in a header string
                    if (n >= chars.Length)
                        return null;
                    chars[n++] = (char)sym;
                    node = 0;
                    bitsSinceSymbol = 0;
                    tailAllOnes = true;
                }
                else
                {
                    bitsSinceSymbol++;
                    if (bitVal == 0)
                        tailAllOnes = false;
                }
            }
        }
        // Trailing partial bits: at most 7 and all ones (a prefix of EOS).
        if (bitsSinceSymbol > 7 || !tailAllOnes)
            return null;
        return new string(chars, 0, n);
    }

    private static int[] _huffLeft;
    private static int[] _huffRight;
    private static int[] _huffSym;
    private static int _huffNodes;

    private static void BuildHuffmanTree()
    {
        if (_huffLeft != null)
            return;
        // 257 symbols x up to 30 bits; a shared-prefix tree stays small.
        _huffLeft = new int[2048];
        _huffRight = new int[2048];
        _huffSym = new int[2048];
        for (int i = 0; i < 2048; i++)
        {
            _huffLeft[i] = -1;
            _huffRight[i] = -1;
            _huffSym[i] = -1;
        }
        _huffNodes = 1;
        for (int sym = 0; sym < 257; sym++)
        {
            uint code = HpackTables.HuffmanCodes[sym];
            int len = HpackTables.HuffmanLengths[sym];
            int node = 0;
            for (int bit = len - 1; bit >= 0; bit--)
            {
                int bitVal = (int)((code >> bit) & 1);
                int next = bitVal != 0 ? _huffRight[node] : _huffLeft[node];
                if (next < 0)
                {
                    if (_huffNodes >= 2048)
                        return;   // cannot happen with the RFC table
                    next = _huffNodes++;
                    if (bitVal != 0)
                        _huffRight[node] = next;
                    else
                        _huffLeft[node] = next;
                }
                node = next;
            }
            _huffSym[node] = sym;
        }
    }
}

/// <summary>HPACK encoding side (server responses: static-table only).</summary>
public static class HpackEncoder
{
    /// <summary>
    /// Encode one header pair into <paramref name="buf"/> at
    /// <paramref name="pos"/>; returns the new position.
    /// </summary>
    public static int WriteHeader(byte[] buf, int pos, string name, string value)
    {
        int exact = Hpack.FindStatic(name, value);
        if (exact > 0)
            return Hpack.WriteInt(buf, pos, exact, 7, 0x80);

        int nameOnly = Hpack.FindStaticName(name);
        if (nameOnly > 0)
        {
            pos = Hpack.WriteInt(buf, pos, nameOnly, 6, 0x40);   // incremental indexing
        }
        else
        {
            pos = Hpack.WriteInt(buf, pos, 0, 6, 0x40);
            pos = Hpack.WriteString(buf, pos, name);
        }
        return Hpack.WriteString(buf, pos, value);
    }
}
