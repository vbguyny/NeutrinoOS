// ProtonOS DDK - X25519 (Curve25519 ECDH) (Phase 6)
//
// Managed C# X25519 (RFC 7748) with radix-2^51 field arithmetic
// (the classic ref10 layout) and a hand-rolled 64x64->128 multiply,
// so the Tier-0 JIT only needs ordinary 32/64-bit integer ops.
//
// Used by: SSH key exchange curve25519-sha256 (RFC 8731) and the
// TLS 1.3 x25519 key_share.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>X25519 scalar multiplication (RFC 7748); see file header.</summary>
public static class X25519
{
    /// <summary>Size of scalars, u-coordinates and outputs (32 bytes).</summary>
    public const int Size = 32;

    private const ulong Mask51 = (1UL << 51) - 1;

    /// <summary>2p in limb form, used to keep subtractions non-negative:
    /// (2^52-38) + (2^52-2)*(2^51 + 2^102 + 2^153 + 2^204) = 2^256 - 38 = 2p.</summary>
    private const ulong TwoP0 = 0xFFFFFFFFFFFDAUL;   // 2^52 - 38
    private const ulong TwoP1 = 0xFFFFFFFFFFFFEUL;   // 2^52 - 2

    /// <summary>Public key for a secret scalar (scalar * base point).</summary>
    public static byte[] ScalarMultBase(byte[] scalar) => ScalarMult(scalar, null);

    /// <summary>
    /// X25519(scalar, u). u == null selects the base point (9).
    /// The scalar is clamped and the u-coordinate top bit masked (RFC 7748).
    /// </summary>
    public static byte[] ScalarMult(byte[] scalar, byte[] u)
    {
        if (scalar == null || scalar.Length != 32)
            throw new ArgumentException("scalar must be 32 bytes");
        if (u != null && u.Length != 32)
            throw new ArgumentException("u must be 32 bytes");

        // Clamp the scalar.
        var k = new byte[32];
        for (int i = 0; i < 32; i++)
            k[i] = scalar[i];
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;

        byte[] uBytes = u;
        if (uBytes == null)
        {
            uBytes = new byte[32];
            uBytes[0] = 9;
        }

        var x1 = new ulong[5];
        FromBytes(x1, uBytes);

        var x2 = new ulong[5];
        var z2 = new ulong[5];
        var x3 = new ulong[5];
        var z3 = new ulong[5];
        x2[0] = 1;
        x3[0] = x1[0]; x3[1] = x1[1]; x3[2] = x1[2]; x3[3] = x1[3]; x3[4] = x1[4];
        z3[0] = 1;

        var a = new ulong[5];
        var aa = new ulong[5];
        var b = new ulong[5];
        var bb = new ulong[5];
        var e = new ulong[5];
        var c = new ulong[5];
        var d = new ulong[5];
        var da = new ulong[5];
        var cb = new ulong[5];
        var tmp = new ulong[5];
        var a24 = new ulong[5];
        a24[0] = 121665;

        // Montgomery ladder over bits 254..0 (RFC 7748 section 5).
        ulong swap = 0;
        for (int t = 254; t >= 0; t--)
        {
            ulong kt = (ulong)((k[t >> 3] >> (t & 7)) & 1);
            swap ^= kt;
            Cswap(swap, x2, x3);
            Cswap(swap, z2, z3);
            swap = kt;

            Add(a, x2, z2);
            Mul(aa, a, a);                 // AA = A^2
            Sub(b, x2, z2);
            Mul(bb, b, b);                 // BB = B^2
            Sub(e, aa, bb);                // E = AA - BB
            Add(c, x3, z3);
            Sub(d, x3, z3);
            Mul(da, d, a);                 // DA = D * A
            Mul(cb, c, b);                 // CB = C * B
            Add(tmp, da, cb);
            Mul(x3, tmp, tmp);             // x3 = (DA + CB)^2
            Sub(tmp, da, cb);
            Mul(tmp, tmp, tmp);            // (DA - CB)^2
            Mul(z3, x1, tmp);              // z3 = x1 * (DA - CB)^2
            Mul(x2, aa, bb);               // x2 = AA * BB
            Mul(tmp, e, a24);              // a24 * E
            Add(tmp, aa, tmp);             // AA + a24 * E
            Mul(z2, e, tmp);               // z2 = E * (AA + a24 * E)
        }

        Cswap(swap, x2, x3);
        Cswap(swap, z2, z3);

        // Return x2 * z2^(p-2) as 32 canonical bytes.
        Invert(tmp, z2);
        Mul(x2, x2, tmp);

        var result = new byte[32];
        ToBytes(result, x2);
        return result;
    }

    // ==================== Field arithmetic (radix 2^51) ====================

    private static void Add(ulong[] r, ulong[] a, ulong[] b)
    {
        r[0] = a[0] + b[0];
        r[1] = a[1] + b[1];
        r[2] = a[2] + b[2];
        r[3] = a[3] + b[3];
        r[4] = a[4] + b[4];
    }

    private static void Sub(ulong[] r, ulong[] a, ulong[] b)
    {
        // r = a - b + 2p (never underflows for inputs with limbs < 2^52).
        r[0] = a[0] + TwoP0 - b[0];
        r[1] = a[1] + TwoP1 - b[1];
        r[2] = a[2] + TwoP1 - b[2];
        r[3] = a[3] + TwoP1 - b[3];
        r[4] = a[4] + TwoP1 - b[4];
    }

    private static void Cswap(ulong swap, ulong[] a, ulong[] b)
    {
        ulong mask = 0UL - swap;
        for (int i = 0; i < 5; i++)
        {
            ulong t = mask & (a[i] ^ b[i]);
            a[i] ^= t;
            b[i] ^= t;
        }
    }

    /// <summary>Full 64x64 -> 128 bit multiply via 32-bit halves.</summary>
    private static void Mul64(ulong x, ulong y, out ulong hi, out ulong lo)
    {
        ulong x0 = x & 0xFFFFFFFFUL;
        ulong x1 = x >> 32;
        ulong y0 = y & 0xFFFFFFFFUL;
        ulong y1 = y >> 32;

        ulong p00 = x0 * y0;
        ulong p01 = x0 * y1;
        ulong p10 = x1 * y0;
        ulong p11 = x1 * y1;

        ulong mid = (p00 >> 32) + (p01 & 0xFFFFFFFFUL) + (p10 & 0xFFFFFFFFUL);
        lo = (p00 & 0xFFFFFFFFUL) | (mid << 32);
        hi = p11 + (p01 >> 32) + (p10 >> 32) + (mid >> 32);
    }

    private static void MulAdd(ulong x, ulong y, ref ulong hi, ref ulong lo)
    {
        Mul64(x, y, out ulong ph, out ulong pl);
        ulong nlo = lo + pl;
        if (nlo < pl)
            ph++;
        lo = nlo;
        hi += ph;
    }

    /// <summary>Accumulate 19 * x * y into a 128-bit accumulator.</summary>
    private static void MulAdd19(ulong x, ulong y, ref ulong hi, ref ulong lo)
    {
        Mul64(x, y, out ulong ph, out ulong pl);

        // p19 = 19 * (ph,pl) = 16p + 2p + p.
        ulong p16 = pl << 4;
        ulong c16 = pl >> 60;
        ulong p2 = pl << 1;
        ulong c2 = pl >> 63;
        ulong low = p16 + p2;
        ulong car = c16 + c2 + (low < p16 ? 1UL : 0UL);
        ulong pl19 = low + pl;
        car += (pl19 < low) ? 1UL : 0UL;
        ulong ph19 = ph * 19 + car;

        ulong nlo = lo + pl19;
        if (nlo < pl19)
            ph19++;
        lo = nlo;
        hi += ph19;
    }

    /// <summary>Field multiplication mod 2^255-19 (interleaved 128-bit sums).</summary>
    private static void Mul(ulong[] r, ulong[] a, ulong[] b)
    {
        ulong t0lo, t0hi, t1lo, t1hi, t2lo, t2hi, t3lo, t3hi, t4lo, t4hi;

        // 2^255 = 19 (mod p): limbs five apart wrap with a factor 19.
        Mul64(a[0], b[0], out t0hi, out t0lo);
        MulAdd19(a[1], b[4], ref t0hi, ref t0lo);
        MulAdd19(a[2], b[3], ref t0hi, ref t0lo);
        MulAdd19(a[3], b[2], ref t0hi, ref t0lo);
        MulAdd19(a[4], b[1], ref t0hi, ref t0lo);

        Mul64(a[0], b[1], out t1hi, out t1lo);
        MulAdd(a[1], b[0], ref t1hi, ref t1lo);
        MulAdd19(a[2], b[4], ref t1hi, ref t1lo);
        MulAdd19(a[3], b[3], ref t1hi, ref t1lo);
        MulAdd19(a[4], b[2], ref t1hi, ref t1lo);

        Mul64(a[0], b[2], out t2hi, out t2lo);
        MulAdd(a[1], b[1], ref t2hi, ref t2lo);
        MulAdd(a[2], b[0], ref t2hi, ref t2lo);
        MulAdd19(a[3], b[4], ref t2hi, ref t2lo);
        MulAdd19(a[4], b[3], ref t2hi, ref t2lo);

        Mul64(a[0], b[3], out t3hi, out t3lo);
        MulAdd(a[1], b[2], ref t3hi, ref t3lo);
        MulAdd(a[2], b[1], ref t3hi, ref t3lo);
        MulAdd(a[3], b[0], ref t3hi, ref t3lo);
        MulAdd19(a[4], b[4], ref t3hi, ref t3lo);

        Mul64(a[0], b[4], out t4hi, out t4lo);
        MulAdd(a[1], b[3], ref t4hi, ref t4lo);
        MulAdd(a[2], b[2], ref t4hi, ref t4lo);
        MulAdd(a[3], b[1], ref t4hi, ref t4lo);
        MulAdd(a[4], b[0], ref t4hi, ref t4lo);

        // Carry chain.
        ulong c = (t0lo >> 51) | (t0hi << 13);
        ulong r0 = t0lo & Mask51;
        ulong n1 = t1lo + c;
        if (n1 < c) t1hi++;
        c = (n1 >> 51) | (t1hi << 13);
        ulong r1 = n1 & Mask51;
        ulong n2 = t2lo + c;
        if (n2 < c) t2hi++;
        c = (n2 >> 51) | (t2hi << 13);
        ulong r2 = n2 & Mask51;
        ulong n3 = t3lo + c;
        if (n3 < c) t3hi++;
        c = (n3 >> 51) | (t3hi << 13);
        ulong r3 = n3 & Mask51;
        ulong n4 = t4lo + c;
        if (n4 < c) t4hi++;
        c = (n4 >> 51) | (t4hi << 13);
        ulong r4 = n4 & Mask51;

        // Wrap the top carry (2^255 = 19) into limb 0.
        r0 += c * 19;
        c = r0 >> 51;
        r0 &= Mask51;
        r1 += c;

        r[0] = r0;
        r[1] = r1;
        r[2] = r2;
        r[3] = r3;
        r[4] = r4;
    }

    /// <summary>Field inversion: output = z^(2^255 - 21) = z^(p-2).</summary>
    private static void Invert(ulong[] output, ulong[] z)
    {
        // p - 2 = 2^255 - 21 = 0x7FFF...FFEB (32-byte big-endian).
        var result = new ulong[5];
        bool started = false;

        for (int byteIdx = 0; byteIdx < 32; byteIdx++)
        {
            int v = byteIdx == 0 ? 0x7F : (byteIdx == 31 ? 0xEB : 0xFF);
            for (int bit = 7; bit >= 0; bit--)
            {
                bool set = ((v >> bit) & 1) != 0;
                if (!started)
                {
                    if (!set)
                        continue;
                    for (int i = 0; i < 5; i++)
                        result[i] = z[i];
                    started = true;
                    continue;
                }
                Mul(result, result, result);
                if (set)
                    Mul(result, result, z);
            }
        }

        for (int i = 0; i < 5; i++)
            output[i] = result[i];
    }

    // ==================== Byte conversion ====================

    private static ulong Load64(byte[] s, int i)
        => (ulong)s[i] | ((ulong)s[i + 1] << 8) | ((ulong)s[i + 2] << 16) | ((ulong)s[i + 3] << 24) |
           ((ulong)s[i + 4] << 32) | ((ulong)s[i + 5] << 40) | ((ulong)s[i + 6] << 48) | ((ulong)s[i + 7] << 56);

    private static void Store64(byte[] s, int i, ulong v)
    {
        for (int b = 0; b < 8; b++)
            s[i + b] = (byte)(v >> (b * 8));
    }

    private static void FromBytes(ulong[] h, byte[] s)
    {
        h[0] = Load64(s, 0) & Mask51;
        h[1] = (Load64(s, 6) >> 3) & Mask51;
        h[2] = (Load64(s, 12) >> 6) & Mask51;
        h[3] = (Load64(s, 19) >> 1) & Mask51;
        h[4] = (Load64(s, 24) >> 12) & Mask51;
    }

    private static void ToBytes(byte[] s, ulong[] hIn)
    {
        ulong h0 = hIn[0], h1 = hIn[1], h2 = hIn[2], h3 = hIn[3], h4 = hIn[4];

        // Reduce fully mod p: add 19, let the carry chain decide whether
        // that wrapped past 2^255, fold the decision back in.
        ulong q = (h0 + 19) >> 51;
        q = (h1 + q) >> 51;
        q = (h2 + q) >> 51;
        q = (h3 + q) >> 51;
        q = (h4 + q) >> 51;
        h0 += 19 * q;

        h1 += h0 >> 51; h0 &= Mask51;
        h2 += h1 >> 51; h1 &= Mask51;
        h3 += h2 >> 51; h2 &= Mask51;
        h4 += h3 >> 51; h3 &= Mask51;
        h4 &= Mask51;   // drop the bit past 2^255

        Store64(s, 0, h0 | (h1 << 51));
        Store64(s, 8, (h1 >> 13) | (h2 << 38));
        Store64(s, 16, (h2 >> 26) | (h3 << 25));
        Store64(s, 24, (h3 >> 39) | (h4 << 12));
    }
}
