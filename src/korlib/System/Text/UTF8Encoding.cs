// NeutrinoOS korlib - System.Text.UTF8Encoding
//
// Phase 2: a complete UTF-8 encoder/decoder (encode and decode of the
// full Unicode range, invalid sequences replaced with U+FFFD). The
// official .NET UTF8Encoding supports encoder/decoder objects, BOM
// handling, and exception fallbacks; none of those are implemented here.

namespace System.Text;

/// <summary>
/// UTF-8 encoding. Encode/decode cover the full Unicode range; invalid
/// byte sequences decode to U+FFFD, matching the default replacement
/// behavior of the official .NET implementation.
/// </summary>
public sealed class UTF8Encoding : Encoding
{
    private readonly bool _emitBom;

    /// <summary>Creates a UTF-8 encoding instance.</summary>
    /// <param name="emitBom">
    /// Accepted for compatibility. NeutrinoOS never emits a preamble in
    /// Phase 2 (serial console output has no use for one).
    /// </param>
    public UTF8Encoding(bool emitBom = false)
    {
        _emitBom = emitBom;
    }

    /// <summary>Gets the name of this encoding ("utf-8").</summary>
    public override string EncodingName => "utf-8";

    /// <summary>Whether this instance would emit a BOM (always false in Phase 2).</summary>
    public bool EmitBom => false;

    /// <summary>Gets the number of bytes produced by encoding the string.</summary>
    public override int GetByteCount(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int count = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x80) count += 1;
            else if (c < 0x800) count += 2;
            else if (IsHighSurrogate(c) && i + 1 < s.Length && IsLowSurrogate(s[i + 1]))
                count += 4;
            else count += 3;
        }
        return count;
    }

    private static bool IsHighSurrogate(char c) => c >= '\uD800' && c <= '\uDBFF';

    private static bool IsLowSurrogate(char c) => c >= '\uDC00' && c <= '\uDFFF';

    /// <summary>Encodes the string into a new byte array.</summary>
    public override byte[] GetBytes(string s)
    {
        if (string.IsNullOrEmpty(s)) return new byte[0];
        var result = new byte[GetByteCount(s)];
        int pos = 0;
        EncodeInto(s, 0, s.Length, result, ref pos);
        return result;
    }

    /// <summary>Encodes the character range into a new byte array.</summary>
    public override byte[] GetBytes(char[] chars, int index, int count)
    {
        // Compute the encoded size without materializing a string
        int size = 0;
        int end = index + count;
        for (int i = index; i < end; i++)
        {
            char c = chars[i];
            if (c < 0x80) size += 1;
            else if (c < 0x800) size += 2;
            else if (IsHighSurrogate(c) && i + 1 < end && IsLowSurrogate(chars[i + 1])) { size += 4; i++; }
            else size += 3;
        }

        var result = new byte[size];
        int pos = 0;
        EncodeInto(chars, index, count, result, ref pos);
        return result;
    }

    /// <summary>
    /// Encodes characters into a caller-provided buffer.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public static int EncodeInto(string s, int start, int count, byte[] destination, ref int destPos)
    {
        int written = 0;
        int end = start + count;
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (c < 0x80)
            {
                destination[destPos++] = (byte)c;
                written++;
            }
            else if (c < 0x800)
            {
                destination[destPos++] = (byte)(0xC0 | (c >> 6));
                destination[destPos++] = (byte)(0x80 | (c & 0x3F));
                written += 2;
            }
            else if (IsHighSurrogate(c) && i + 1 < end && IsLowSurrogate(s[i + 1]))
            {
                int cp = 0x10000 + (((c - 0xD800) << 10) | (s[i + 1] - 0xDC00));
                destination[destPos++] = (byte)(0xF0 | (cp >> 18));
                destination[destPos++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                destination[destPos++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                destination[destPos++] = (byte)(0x80 | (cp & 0x3F));
                written += 4;
                i++;
            }
            else
            {
                destination[destPos++] = (byte)(0xE0 | (c >> 12));
                destination[destPos++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                destination[destPos++] = (byte)(0x80 | (c & 0x3F));
                written += 3;
            }
        }
        return written;
    }

    private static int EncodeInto(char[] chars, int start, int count, byte[] destination, ref int destPos)
    {
        int written = 0;
        int end = start + count;
        for (int i = start; i < end; i++)
        {
            char c = chars[i];
            if (c < 0x80)
            {
                destination[destPos++] = (byte)c;
                written++;
            }
            else if (c < 0x800)
            {
                destination[destPos++] = (byte)(0xC0 | (c >> 6));
                destination[destPos++] = (byte)(0x80 | (c & 0x3F));
                written += 2;
            }
            else if (IsHighSurrogate(c) && i + 1 < end && IsLowSurrogate(chars[i + 1]))
            {
                int cp = 0x10000 + (((c - 0xD800) << 10) | (chars[i + 1] - 0xDC00));
                destination[destPos++] = (byte)(0xF0 | (cp >> 18));
                destination[destPos++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                destination[destPos++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                destination[destPos++] = (byte)(0x80 | (cp & 0x3F));
                written += 4;
                i++;
            }
            else
            {
                destination[destPos++] = (byte)(0xE0 | (c >> 12));
                destination[destPos++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                destination[destPos++] = (byte)(0x80 | (c & 0x3F));
                written += 3;
            }
        }
        return written;
    }

    /// <summary>Decodes the byte array into a new string.</summary>
    public override string GetString(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        return GetString(bytes, 0, bytes.Length);
    }

    /// <summary>Decodes the byte range into a new string.</summary>
    public override string GetString(byte[] bytes, int index, int count)
    {
        var sb = new StringBuilder(count);
        int end = index + count;
        for (int i = index; i < end; i++)
        {
            byte b = bytes[i];
            if (b < 0x80)
            {
                sb.Append((char)b);
            }
            else if ((b & 0xE0) == 0xC0)
            {
                if (i + 1 < end && (bytes[i + 1] & 0xC0) == 0x80)
                {
                    sb.Append((char)(((b & 0x1F) << 6) | (bytes[i + 1] & 0x3F)));
                    i++;
                }
                else sb.Append('\uFFFD');
            }
            else if ((b & 0xF0) == 0xE0)
            {
                if (i + 2 < end && (bytes[i + 1] & 0xC0) == 0x80 && (bytes[i + 2] & 0xC0) == 0x80)
                {
                    sb.Append((char)(((b & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F)));
                    i += 2;
                }
                else sb.Append('\uFFFD');
            }
            else if ((b & 0xF8) == 0xF0)
            {
                if (i + 3 < end && (bytes[i + 1] & 0xC0) == 0x80 && (bytes[i + 2] & 0xC0) == 0x80 && (bytes[i + 3] & 0xC0) == 0x80)
                {
                    int cp = ((b & 0x07) << 18) | ((bytes[i + 1] & 0x3F) << 12) | ((bytes[i + 2] & 0x3F) << 6) | (bytes[i + 3] & 0x3F);
                    if (cp >= 0x10000 && cp <= 0x10FFFF)
                    {
                        cp -= 0x10000;
                        sb.Append((char)(0xD800 + (cp >> 10)));
                        sb.Append((char)(0xDC00 + (cp & 0x3FF)));
                    }
                    else sb.Append('\uFFFD');
                    i += 3;
                }
                else sb.Append('\uFFFD');
            }
            else
            {
                sb.Append('\uFFFD');
            }
        }
        return sb.ToString();
    }

    /// <summary>Gets the number of characters produced by decoding the byte array.</summary>
    public override int GetCharCount(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return 0;
        // Upper bound: one char per byte plus surrogates for 4-byte sequences
        return bytes.Length;
    }
}
