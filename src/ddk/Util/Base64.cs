// ProtonOS DDK - Base64 (Phase 6)
// Standard RFC 4648 base64 with '=' padding. Used for authorized_keys
// parsing and SSH public-key blobs.

using System;

namespace ProtonOS.DDK.Util;

/// <summary>Standard base64 encoding/decoding.</summary>
public static class Base64
{
    private const string Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>Encode bytes to base64.</summary>
    public static string Encode(byte[] data)
    {
        if (data == null || data.Length == 0)
            return "";

        var chars = new char[((data.Length + 2) / 3) * 4];
        int ci = 0;
        int i = 0;
        while (i + 3 <= data.Length)
        {
            int n = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
            chars[ci++] = Alphabet[(n >> 18) & 63];
            chars[ci++] = Alphabet[(n >> 12) & 63];
            chars[ci++] = Alphabet[(n >> 6) & 63];
            chars[ci++] = Alphabet[n & 63];
            i += 3;
        }
        int rem = data.Length - i;
        if (rem == 1)
        {
            int n = data[i] << 16;
            chars[ci++] = Alphabet[(n >> 18) & 63];
            chars[ci++] = Alphabet[(n >> 12) & 63];
            chars[ci++] = '=';
            chars[ci++] = '=';
        }
        else if (rem == 2)
        {
            int n = (data[i] << 16) | (data[i + 1] << 8);
            chars[ci++] = Alphabet[(n >> 18) & 63];
            chars[ci++] = Alphabet[(n >> 12) & 63];
            chars[ci++] = Alphabet[(n >> 6) & 63];
            chars[ci++] = '=';
        }
        return new string(chars, 0, ci);
    }

    /// <summary>Decode base64; returns null on invalid input.</summary>
    public static byte[] Decode(string s)
    {
        if (s == null)
            return null;
        // Strip whitespace and padding.
        int end = s.Length;
        while (end > 0 && (s[end - 1] == '=' || s[end - 1] == '\n' || s[end - 1] == '\r'))
            end--;
        int start = 0;
        while (start < end && (s[start] == ' ' || s[start] == '\t'))
            start++;

        int len = end - start;
        if (len == 0)
            return new byte[0];
        int outLen = (len / 4) * 3;
        int rem = len % 4;
        if (rem == 2)
            outLen += 1;
        else if (rem == 3)
            outLen += 2;

        var result = new byte[outLen];
        int bits = 0;
        int acc = 0;
        int oi = 0;
        for (int i = start; i < end; i++)
        {
            int v = ValueOf(s[i]);
            if (v < 0)
            {
                if (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')
                    continue;
                return null;
            }
            acc = (acc << 6) | v;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                result[oi++] = (byte)(acc >> bits);
            }
        }
        return result;
    }

    private static int ValueOf(char c)
    {
        if (c >= 'A' && c <= 'Z')
            return c - 'A';
        if (c >= 'a' && c <= 'z')
            return c - 'a' + 26;
        if (c >= '0' && c <= '9')
            return c - '0' + 52;
        if (c == '+')
            return 62;
        if (c == '/')
            return 63;
        return -1;
    }
}
