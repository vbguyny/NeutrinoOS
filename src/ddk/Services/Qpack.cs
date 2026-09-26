// ProtonOS Phase 9: QPACK (RFC 9204) codec - static table only.
//
// The server advertises SETTINGS_QPACK_MAX_TABLE_CAPACITY = 0, so dynamic
// table instructions never appear in requests and every field section
// starts with Required Insert Count 0. Header field values are sent
// without Huffman coding (always legal); Huffman DECODING is required
// for real clients and reuses the HPACK/RFC 7541 code table (the wire
// format is identical).
namespace ProtonOS.DDK.Services;

public static class Qpack
{
    public static bool StrEq(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    // ==================== integers / strings ====================

    /// <summary>N-bit prefix integer (RFC 7541 5.1 semantics). Returns new position.</summary>
    public static int WriteInt(byte[] buf, int pos, int prefixBits, int value)
    {
        int max = (1 << prefixBits) - 1;
        int first = buf[pos];   // preserve upper bits already in the byte
        if (value < max)
        {
            buf[pos] = (byte)(first | value);
            return pos + 1;
        }
        buf[pos] = (byte)(first | max);
        value -= max;
        while (value >= 128)
        {
            buf[++pos] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        buf[++pos] = (byte)value;
        return pos + 1;
    }

    /// <summary>Length-prefixed string, no Huffman. Returns new position.</summary>
    public static int WriteString(byte[] buf, int pos, string s)
    {
        int len = s == null ? 0 : s.Length;
        buf[pos] = 0;
        pos = WriteInt(buf, pos, 7, len);
        for (int i = 0; i < len; i++)
            buf[pos++] = (byte)s[i];
        return pos;
    }

    /// <summary>Read an N-bit prefix integer; -1 on malformed input.</summary>
    public static int ReadInt(byte[] buf, int pos, int end, int prefixBits, out int value)
    {
        value = 0;
        if (pos >= end)
            return -1;
        int max = (1 << prefixBits) - 1;
        value = buf[pos] & max;
        pos++;
        if (value < max)
            return pos;
        int shift = 0;
        while (pos < end)
        {
            int b = buf[pos++];
            value += (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return pos;
            shift += 7;
            if (shift > 21)
                return -1;
        }
        return -1;
    }

    /// <summary>Read a string (H bit honored via the 7541 Huffman decoder); -1 on error.</summary>
    public static int ReadString(byte[] buf, int pos, int end, out string s)
    {
        s = null;
        if (pos >= end)
            return -1;
        bool huffman = (buf[pos] & 0x80) != 0;
        int len;
        pos = ReadInt(buf, pos, end, 7, out len);
        if (pos < 0 || len < 0 || pos + len > end)
            return -1;
        if (huffman)
        {
            s = HpackDecoder.HuffmanDecode(buf, pos, len);
            if (s == null)
                return -1;
        }
        else
        {
            var chars = new char[len];
            for (int i = 0; i < len; i++)
                chars[i] = (char)buf[pos + i];
            s = new string(chars);
        }
        return pos + len;
    }

    // ==================== static table ====================

    public static int FindStatic(string name, string value)
    {
        for (int i = 0; i < QpackTables.StaticCount; i++)
        {
            if (StrEq(QpackTables.StaticNames[i], name) &&
                StrEq(QpackTables.StaticValues[i], value))
                return i;
        }
        return -1;
    }

    public static int FindStaticName(string name)
    {
        for (int i = 0; i < QpackTables.StaticCount; i++)
        {
            if (StrEq(QpackTables.StaticNames[i], name))
                return i;
        }
        return -1;
    }

    // ==================== field section encode ====================

    /// <summary>
    /// Encode a field section into buf. Layout: Required Insert Count (8-bit
    /// prefix 0), Base (7-bit prefix 0), then one of indexed / literal-with-
    /// name-reference / literal per field.
    /// </summary>
    public static int EncodeFieldSection(byte[] buf, int pos, string[] names, string[] values, int count)
    {
        buf[pos] = 0;                 // Required Insert Count = 0
        pos = WriteInt(buf, pos, 8, 0);
        buf[pos] = 0;                 // Base = 0 (S bit clear)
        pos = WriteInt(buf, pos, 7, 0);

        for (int i = 0; i < count; i++)
        {
            string name = names[i];
            string value = values[i];

            int exact = FindStatic(name, value);
            if (exact >= 0)
            {
                // Indexed field line: 1 T Index (T=1 static)
                buf[pos] = 0xC0;
                pos = WriteInt(buf, pos, 6, exact);
                continue;
            }

            int nameIdx = FindStaticName(name);
            if (nameIdx >= 0)
            {
                // Literal with name reference: 01 N T Index
                // (N=0 never-indexed clear, T=1 STATIC table reference -
                //  omitting the T bit made clients read a dynamic-table
                //  reference and fail with QPACK_DECODER_STREAM_ERROR).
                buf[pos] = 0x50;
                pos = WriteInt(buf, pos, 4, nameIdx);
            }
            else
            {
                // Literal with literal name: 001 N T Length
                buf[pos] = 0x28;
                pos = WriteInt(buf, pos, 3, name.Length);
                for (int k = 0; k < name.Length; k++)
                    buf[pos++] = (byte)name[k];
            }
            pos = WriteString(buf, pos, value);
        }
        return pos;
    }

    // ==================== field section decode ====================

    /// <summary>
    /// Decode a field section. Returns the number of fields, or -1 when
    /// malformed. Dynamic references are rejected (capacity 0).
    /// </summary>
    public static int DecodeFieldSection(byte[] buf, int pos, int len,
        string[] outNames, string[] outValues, int maxCount)
    {
        int end = pos + len;
        int ric;
        pos = ReadInt(buf, pos, end, 8, out ric);
        if (pos < 0 || ric != 0)
            return -1;
        int b = buf[pos];
        bool sign = (b & 0x40) != 0;
        int deltaBase;
        pos = ReadInt(buf, pos, end, 7, out deltaBase);
        if (pos < 0)
            return -1;
        if (sign != (deltaBase == 0 && ric == 0))
        {
            // zero-base encoding has S=0; anything else would reference
            // the (empty) dynamic table
            if (deltaBase != 0)
                return -1;
        }

        int count = 0;
        while (pos < end)
        {
            if (count >= maxCount)
                return -1;
            int first = buf[pos];
            if ((first & 0x80) != 0)
            {
                // Indexed field line: 1 T Index
                bool static_ = (first & 0x40) != 0;
                int idx;
                pos = ReadInt(buf, pos, end, 6, out idx);
                if (pos < 0 || !static_ || idx >= QpackTables.StaticCount)
                    return -1;
                outNames[count] = QpackTables.StaticNames[idx];
                outValues[count] = QpackTables.StaticValues[idx];
                count++;
            }
            else if ((first & 0x40) != 0)
            {
                // Literal with name reference: 01 N T Index
                bool static_ = (first & 0x10) != 0;
                int idx;
                pos = ReadInt(buf, pos, end, 4, out idx);
                if (pos < 0 || !static_ || idx >= QpackTables.StaticCount)
                    return -1;
                string value;
                pos = ReadString(buf, pos, end, out value);
                if (pos < 0)
                    return -1;
                outNames[count] = QpackTables.StaticNames[idx];
                outValues[count] = value;
                count++;
            }
            else if ((first & 0x20) != 0)
            {
                // Literal with literal name: 001 N T NameLength ...
                int nameLen;
                pos = ReadInt(buf, pos, end, 3, out nameLen);
                if (pos < 0 || nameLen < 0 || pos + nameLen > end)
                    return -1;
                var chars = new char[nameLen];
                for (int i = 0; i < nameLen; i++)
                    chars[i] = (char)buf[pos + i];
                pos += nameLen;
                string value;
                pos = ReadString(buf, pos, end, out value);
                if (pos < 0)
                    return -1;
                outNames[count] = new string(chars);
                outValues[count] = value;
                count++;
            }
            else
            {
                // 0001xxxx / 0000xxxx: encoder/decoder stream instructions
                // never occur inside a field section.
                return -1;
            }
        }
        return count;
    }
}
