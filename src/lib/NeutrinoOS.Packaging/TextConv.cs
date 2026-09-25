// NeutrinoOS Phase 8 - shared packaging library (NeutrinoOS.Packaging).
//
// TextConv: hand-rolled integer/hex/CRC helpers used by every other file in
// this library.  This source is compiled BOTH into the on-device npkg
// utility (korlib subset, Tier-0 JIT) and into the host-side packaging
// tools (desktop .NET), so it must not use System.Math, Span<T>,
// CultureInfo, int.ToString()/long.ToString(), string.Format or any other
// API that the korlib subset does not provide.

using System;
using System.Text;

namespace NeutrinoOS.Packaging
{
    /// <summary>
    /// Phase 8 text helpers shared by the npkg package manager (on device)
    /// and the host packaging tools: decimal integer formatting/parsing
    /// without the BCL ToString/Parse family, lowercase hex encode/decode,
    /// and the standard ZIP CRC-32.
    /// </summary>
    public static class TextConv
    {
        // Lazily built CRC-32 table (polynomial 0xEDB88320).  Built on first
        // use so no static constructor is required (Tier-0 JIT friendly).
        private static uint[] _crcTable;

        /// <summary>
        /// Phase 8: formats a 64-bit signed integer in decimal without using
        /// long.ToString(). Handles long.MinValue exactly.
        /// </summary>
        public static string LongToString(long v)
        {
            if (v == long.MinValue)
                return "-9223372036854775808";

            bool negative = v < 0;
            ulong magnitude = negative ? (ulong)(-v) : (ulong)v;

            char[] digits = new char[20];
            int n = 0;
            do
            {
                digits[n] = (char)('0' + (int)(magnitude % 10));
                magnitude = magnitude / 10;
                n++;
            }
            while (magnitude != 0);

            StringBuilder sb = new StringBuilder(n + (negative ? 1 : 0));
            if (negative)
                sb.Append('-');
            for (int i = n - 1; i >= 0; i--)
                sb.Append(digits[i]);
            return sb.ToString();
        }

        /// <summary>
        /// Phase 8: parses an optional leading '-' followed by decimal digits
        /// into a 64-bit integer without using long.Parse. Returns false for
        /// empty input, non-digit characters and overflow.
        /// </summary>
        public static bool TryParseLong(string s, out long v)
        {
            v = 0;
            if (s == null)
                return false;

            int i = 0;
            bool negative = false;
            if (i < s.Length && s[i] == '-')
            {
                negative = true;
                i++;
            }
            if (i >= s.Length)
                return false;

            ulong limit = negative ? 9223372036854775808UL : 9223372036854775807UL;
            ulong acc = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c < '0' || c > '9')
                    return false;
                ulong digit = (ulong)(c - '0');
                if (acc > (limit - digit) / 10)
                    return false; // would overflow
                acc = acc * 10 + digit;
                i++;
            }

            if (negative)
            {
                if (acc == 9223372036854775808UL)
                    v = long.MinValue;
                else
                    v = -(long)acc;
            }
            else
            {
                v = (long)acc;
            }
            return true;
        }

        /// <summary>
        /// Phase 8: lower-case hex encoding of a byte array (used for
        /// SHA-256 checksums, Ed25519 key fingerprints and signatures).
        /// </summary>
        public static string HexEncode(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
            {
                int b = data[i];
                sb.Append(HexDigit(b >> 4));
                sb.Append(HexDigit(b & 0x0F));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Phase 8: decodes hex text (upper or lower case) into bytes.
        /// Returns null when the text has odd length or contains a
        /// non-hex character; never throws for malformed input.
        /// </summary>
        public static byte[] HexDecode(string hex)
        {
            if (hex == null || (hex.Length & 1) != 0)
                return null;

            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = HexValue(hex[i * 2]);
                int lo = HexValue(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0)
                    return null;
                result[i] = (byte)((hi << 4) | lo);
            }
            return result;
        }

        /// <summary>
        /// Phase 8: standard ZIP CRC-32 (reflected polynomial 0xEDB88320,
        /// initial/final XOR 0xFFFFFFFF) over a byte range; matches the CRC
        /// stored in .npkg ZIP entries. The 256-entry table is built lazily
        /// on first use.
        /// </summary>
        public static uint Crc32(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException("data");
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException("offset");

            uint[] table = _crcTable;
            if (table == null)
            {
                table = new uint[256];
                for (int i = 0; i < 256; i++)
                {
                    uint c = (uint)i;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
                _crcTable = table;
            }

            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < count; i++)
                crc = table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static char HexDigit(int v)
        {
            return v < 10 ? (char)('0' + v) : (char)('a' + (v - 10));
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'a' && c <= 'f')
                return c - 'a' + 10;
            if (c >= 'A' && c <= 'F')
                return c - 'A' + 10;
            return -1;
        }
    }
}
