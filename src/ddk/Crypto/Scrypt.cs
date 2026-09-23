// ProtonOS DDK - scrypt password KDF (Phase 6)
//
// Managed C# scrypt (RFC 7914) built on HMAC-SHA256 and the Salsa20/8
// core. Used for /etc/shadow password hashes and for SSH password
// authentication. PBKDF2 is only ever used with c = 1 here.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed scrypt key derivation (see file header).</summary>
public static class Scrypt
{
    /// <summary>
    /// Derive <paramref name="dkLen"/> bytes from password and salt.
    /// Memory use is 128 * r * N bytes for the ROMix buffer.
    /// </summary>
    public static byte[] Derive(byte[] password, byte[] salt, int n, int r, int p, int dkLen)
    {
        if (password == null)
            password = new byte[0];
        if (salt == null)
            salt = new byte[0];
        if (r <= 0 || p <= 0 || dkLen <= 0)
            throw new ArgumentException("invalid scrypt parameters");
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentException("N must be a power of two > 1");

        int blockLen = 128 * r;

        // B = PBKDF2-HMAC-SHA256(P, S, 1, p * 128 * r)
        var b = Pbkdf2(password, salt, p * blockLen, 1);

        // ROMix each of the p blocks. (The V buffer needs 128*r*N bytes;
        // callers choose N so this fits in an int-sized allocation.)
        var x = new byte[blockLen];
        int vLen = n * blockLen;
        var v = new byte[vLen];
        var y = new byte[blockLen];
        for (int i = 0; i < p; i++)
        {
            for (int j = 0; j < blockLen; j++)
                x[j] = b[i * blockLen + j];
            Romix(x, v, y, n, r, blockLen);
            for (int j = 0; j < blockLen; j++)
                b[i * blockLen + j] = x[j];
        }

        // DK = PBKDF2-HMAC-SHA256(P, B, 1, dkLen)
        return Pbkdf2(password, b, dkLen, 1);
    }

    private static void Romix(byte[] x, byte[] v, byte[] y, int n, int r, int blockLen)
    {
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < blockLen; j++)
                v[i * blockLen + j] = x[j];
            BlockMix(x, y, r);
            // x <- y
            for (int j = 0; j < blockLen; j++)
                x[j] = y[j];
        }

        for (int i = 0; i < n; i++)
        {
            // j = Integerify(x) mod N: the first 8 bytes of the last 64-byte block.
            int last = (2 * r - 1) * 64;
            ulong intVal = 0;
            for (int k = 7; k >= 0; k--)
                intVal = (intVal << 8) | x[last + k];
            int j = (int)(intVal & (ulong)(n - 1));

            for (int k = 0; k < blockLen; k++)
                x[k] ^= v[j * blockLen + k];
            BlockMix(x, y, r);
            for (int k = 0; k < blockLen; k++)
                x[k] = y[k];
        }
    }

    /// <summary>BlockMix with Salsa20/8 (RFC 7914 section 4).</summary>
    private static void BlockMix(byte[] b, byte[] y, int r)
    {
        var x = new byte[64];
        // X = B[2r-1]
        int last = (2 * r - 1) * 64;
        for (int i = 0; i < 64; i++)
            x[i] = b[last + i];

        for (int i = 0; i < 2 * r; i++)
        {
            for (int k = 0; k < 64; k++)
                x[k] ^= b[i * 64 + k];
            Salsa20Core8(x);
            // Y[i] goes to position: even i -> i/2, odd i -> r + (i-1)/2
            int dst = (i % 2 == 0) ? (i / 2) : (r + (i - 1) / 2);
            for (int k = 0; k < 64; k++)
                y[dst * 64 + k] = x[k];
        }
    }

    /// <summary>Salsa20/8 core: 8 rounds over a 64-byte block, in place.</summary>
    private static void Salsa20Core8(byte[] block)
    {
        uint[] w = new uint[16];
        for (int i = 0; i < 16; i++)
            w[i] = (uint)block[i * 4] | ((uint)block[i * 4 + 1] << 8) |
                   ((uint)block[i * 4 + 2] << 16) | ((uint)block[i * 4 + 3] << 24);

        uint x0 = w[0], x1 = w[1], x2 = w[2], x3 = w[3];
        uint x4 = w[4], x5 = w[5], x6 = w[6], x7 = w[7];
        uint x8 = w[8], x9 = w[9], x10 = w[10], x11 = w[11];
        uint x12 = w[12], x13 = w[13], x14 = w[14], x15 = w[15];

        for (int round = 0; round < 4; round++)
        {
            // Column round.
            x4 ^= Rotl(x0 + x12, 7); x8 ^= Rotl(x4 + x0, 9);
            x12 ^= Rotl(x8 + x4, 13); x0 ^= Rotl(x12 + x8, 18);
            x9 ^= Rotl(x5 + x1, 7); x13 ^= Rotl(x9 + x5, 9);
            x1 ^= Rotl(x13 + x9, 13); x5 ^= Rotl(x1 + x13, 18);
            x14 ^= Rotl(x10 + x6, 7); x2 ^= Rotl(x14 + x10, 9);
            x6 ^= Rotl(x2 + x14, 13); x10 ^= Rotl(x6 + x2, 18);
            x3 ^= Rotl(x15 + x11, 7); x7 ^= Rotl(x3 + x15, 9);
            x11 ^= Rotl(x7 + x3, 13); x15 ^= Rotl(x11 + x7, 18);
            // Row round.
            x1 ^= Rotl(x0 + x3, 7); x2 ^= Rotl(x1 + x0, 9);
            x3 ^= Rotl(x2 + x1, 13); x0 ^= Rotl(x3 + x2, 18);
            x6 ^= Rotl(x5 + x4, 7); x7 ^= Rotl(x6 + x5, 9);
            x4 ^= Rotl(x7 + x6, 13); x5 ^= Rotl(x4 + x7, 18);
            x11 ^= Rotl(x10 + x9, 7); x8 ^= Rotl(x11 + x10, 9);
            x9 ^= Rotl(x8 + x11, 13); x10 ^= Rotl(x9 + x8, 18);
            x12 ^= Rotl(x15 + x14, 7); x13 ^= Rotl(x12 + x15, 9);
            x14 ^= Rotl(x13 + x12, 13); x15 ^= Rotl(x14 + x13, 18);
        }

        x0 += w[0]; x1 += w[1]; x2 += w[2]; x3 += w[3];
        x4 += w[4]; x5 += w[5]; x6 += w[6]; x7 += w[7];
        x8 += w[8]; x9 += w[9]; x10 += w[10]; x11 += w[11];
        x12 += w[12]; x13 += w[13]; x14 += w[14]; x15 += w[15];

        var outw = new uint[16]
        {
            x0, x1, x2, x3, x4, x5, x6, x7,
            x8, x9, x10, x11, x12, x13, x14, x15,
        };
        for (int i = 0; i < 16; i++)
        {
            uint v = outw[i];
            block[i * 4] = (byte)v;
            block[i * 4 + 1] = (byte)(v >> 8);
            block[i * 4 + 2] = (byte)(v >> 16);
            block[i * 4 + 3] = (byte)(v >> 24);
        }
    }

    private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));

    /// <summary>PBKDF2-HMAC-SHA256 with one iteration (all scrypt uses c = 1).</summary>
    private static byte[] Pbkdf2(byte[] password, byte[] salt, int dkLen, int ignoredIterations)
    {
        int blocks = (dkLen + 31) / 32;
        var result = new byte[dkLen];
        var data = new byte[salt.Length + 4];
        for (int i = 0; i < salt.Length; i++)
            data[i] = salt[i];

        for (int block = 1; block <= blocks; block++)
        {
            data[salt.Length] = (byte)(block >> 24);
            data[salt.Length + 1] = (byte)(block >> 16);
            data[salt.Length + 2] = (byte)(block >> 8);
            data[salt.Length + 3] = (byte)block;
            var t = Hmac.Compute(HashKind.Sha256, password, data);
            int offset = (block - 1) * 32;
            int n = dkLen - offset;
            if (n > 32)
                n = 32;
            for (int i = 0; i < n; i++)
                result[offset + i] = t[i];
        }
        return result;
    }

    // ==================== Password hash format ====================

    /// <summary>
    /// Hash a password for /etc/shadow:
    /// "scrypt$N$r$p$salt_hex$hash_hex" with production parameters.
    /// </summary>
    public static string HashPassword(byte[] password, byte[] salt)
        => HashPasswordWithParams(password, salt, 16384, 8, 1);

    /// <summary>Password hashing with explicit parameters (tests, tools).</summary>
    public static string HashPasswordWithParams(byte[] password, byte[] salt, int n, int r, int p)
    {
        var dk = Derive(password, salt, n, r, p, 32);
        return "scrypt$" + Dec(n) + "$" + Dec(r) + "$" + Dec(p) + "$" +
               ToHex(salt) + "$" + ToHex(dk);
    }

    /// <summary>Verify a password against a HashPassword-format string.</summary>
    public static bool VerifyPassword(byte[] password, string stored)
    {
        if (stored == null)
            return false;

        // Parse "scrypt$N$r$p$salt$hash" with manual scanning (the guest
        // JIT only supports basic string operations).
        var parts = SplitDollar(stored);
        if (parts == null)
            return false;
        if (parts[0].Length != 6 || parts[0][0] != 's' || parts[0][1] != 'c' ||
            parts[0][2] != 'r' || parts[0][3] != 'y' || parts[0][4] != 'p' || parts[0][5] != 't')
            return false;
        if (!TryParseInt(parts[1], out int n))
            return false;
        if (!TryParseInt(parts[2], out int r))
            return false;
        if (!TryParseInt(parts[3], out int pp))
            return false;
        byte[] salt = FromHex(parts[4]);
        byte[] want = FromHex(parts[5]);
        if (salt == null || want == null)
            return false;

        var got = Derive(password, salt, n, r, pp, want.Length);
        int diff = 0;
        for (int i = 0; i < want.Length; i++)
            diff |= got[i] ^ want[i];
        return diff == 0;
    }

    /// <summary>Split "a$b$c$d$e$f" into exactly six parts (last takes
    /// the remainder); null when fewer parts exist.</summary>
    private static string[] SplitDollar(string s)
    {
        const int MaxParts = 6;
        var parts = new string[MaxParts];
        int count = 0;
        int start = 0;
        for (int i = 0; i <= s.Length && count < MaxParts; i++)
        {
            if (i == s.Length || s[i] == '$')
            {
                parts[count] = s.Substring(start, i - start);
                count++;
                start = i + 1;
                if (count == MaxParts - 1 && i < s.Length)
                {
                    parts[count] = s.Substring(start, s.Length - start);
                    count++;
                }
            }
        }
        return count == MaxParts ? parts : null;
    }

    private static bool TryParseInt(string s, out int v)
    {
        v = 0;
        if (s.Length == 0 || s.Length > 9)
            return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                return false;
            v = v * 10 + (c - '0');
        }
        return true;
    }

    private static string Dec(int v)
    {
        if (v == 0)
            return "0";
        string s = "";
        while (v > 0)
        {
            s = Digits[v % 10] + s;
            v /= 10;
        }
        return s;
    }

    private static readonly string[] Digits =
    {
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    };

    private static readonly string[] HexDigits =
    {
        "0", "1", "2", "3", "4", "5", "6", "7",
        "8", "9", "a", "b", "c", "d", "e", "f",
    };

    internal static string ToHex(byte[] data)
    {
        string s = "";
        for (int i = 0; i < data.Length; i++)
        {
            s += HexDigits[data[i] >> 4];
            s += HexDigits[data[i] & 15];
        }
        return s;
    }

    internal static byte[] FromHex(string hex)
    {
        if (hex == null || (hex.Length & 1) != 0)
            return null;
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
        {
            int hi = Nibble(hex[i * 2]);
            int lo = Nibble(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0)
                return null;
            b[i] = (byte)((hi << 4) | lo);
        }
        return b;
    }

    private static int Nibble(char c)
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
