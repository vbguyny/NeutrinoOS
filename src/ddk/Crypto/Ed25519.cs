// ProtonOS DDK - Ed25519 signatures (Phase 6)
//
// Managed C# Ed25519 (RFC 8032) with radix-2^51 field arithmetic and
// the complete twisted-Edwards addition formulas. Scalars are reduced
// mod L with a simple bitwise long division over little-endian byte
// arrays, so nothing beyond ordinary integer ops is needed (Tier-0 JIT).
//
// Used by: SSH host keys (ssh-ed25519) and public-key authentication.
//
// Field layout: 5 limbs of 51 bits, value = l0 + l1*2^51 + ... + l4*2^204.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed Ed25519 (see file header).</summary>
public static class Ed25519
{
    /// <summary>Size of a seed/private key (32 bytes).</summary>
    public const int SeedSize = 32;

    /// <summary>Size of a public key (32 bytes).</summary>
    public const int PublicKeySize = 32;

    /// <summary>Size of a signature (64 bytes).</summary>
    public const int SignatureSize = 64;

    // ==================== Field arithmetic (radix 2^51) ====================

    private const ulong Mask51 = (1UL << 51) - 1;
    private const ulong TwoP0 = 0xFFFFFFFFFFFDAUL;   // 2^52 - 38
    private const ulong TwoP1 = 0xFFFFFFFFFFFFEUL;   // 2^52 - 2

    private static void FeAdd(ulong[] r, ulong[] a, ulong[] b)
    {
        r[0] = a[0] + b[0];
        r[1] = a[1] + b[1];
        r[2] = a[2] + b[2];
        r[3] = a[3] + b[3];
        r[4] = a[4] + b[4];
    }

    private static void FeSub(ulong[] r, ulong[] a, ulong[] b)
    {
        r[0] = a[0] + TwoP0 - b[0];
        r[1] = a[1] + TwoP1 - b[1];
        r[2] = a[2] + TwoP1 - b[2];
        r[3] = a[3] + TwoP1 - b[3];
        r[4] = a[4] + TwoP1 - b[4];
    }

    private static void FeNeg(ulong[] r, ulong[] a)
    {
        r[0] = TwoP0 - a[0];
        r[1] = TwoP1 - a[1];
        r[2] = TwoP1 - a[2];
        r[3] = TwoP1 - a[3];
        r[4] = TwoP1 - a[4];
    }

    private static void FeCopy(ulong[] r, ulong[] a)
    {
        r[0] = a[0];
        r[1] = a[1];
        r[2] = a[2];
        r[3] = a[3];
        r[4] = a[4];
    }

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

    private static void MulAdd19(ulong x, ulong y, ref ulong hi, ref ulong lo)
    {
        Mul64(x, y, out ulong ph, out ulong pl);

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

    private static void FeMul(ulong[] r, ulong[] a, ulong[] b)
    {
        ulong t0lo, t0hi, t1lo, t1hi, t2lo, t2hi, t3lo, t3hi, t4lo, t4hi;

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

    /// <summary>Carry-normalize loose limbs so every limb falls below
    /// 2^51 (mod p, with the 2^255 = 19 wrap). Inputs must have limbs
    /// well below 2^58 for the wrap multiplication to be safe.</summary>
    private static void FeNormalize(ulong[] a)
    {
        ulong c = a[0] >> 51; a[0] &= Mask51; a[1] += c;
        c = a[1] >> 51; a[1] &= Mask51; a[2] += c;
        c = a[2] >> 51; a[2] &= Mask51; a[3] += c;
        c = a[3] >> 51; a[3] &= Mask51; a[4] += c;
        c = a[4] >> 51; a[4] &= Mask51; a[0] += c * 19;
        c = a[0] >> 51; a[0] &= Mask51; a[1] += c;
    }

    private static void FeSq(ulong[] r, ulong[] a) => FeMul(r, a, a);

    private static ulong Load64(byte[] s, int i)
        => (ulong)s[i] | ((ulong)s[i + 1] << 8) | ((ulong)s[i + 2] << 16) | ((ulong)s[i + 3] << 24) |
           ((ulong)s[i + 4] << 32) | ((ulong)s[i + 5] << 40) | ((ulong)s[i + 6] << 48) | ((ulong)s[i + 7] << 56);

    private static void Store64(byte[] s, int i, ulong v)
    {
        for (int b = 0; b < 8; b++)
            s[i + b] = (byte)(v >> (b * 8));
    }

    private static void FeFromBytes(ulong[] h, byte[] s)
    {
        // The top bit (255) is dropped by the masks (RFC 8032 decode).
        h[0] = Load64(s, 0) & Mask51;
        h[1] = (Load64(s, 6) >> 3) & Mask51;
        h[2] = (Load64(s, 12) >> 6) & Mask51;
        h[3] = (Load64(s, 19) >> 1) & Mask51;
        h[4] = (Load64(s, 24) >> 12) & Mask51;
    }

    private static void FeToBytes(byte[] s, ulong[] hIn)
    {
        // First fold any loose limbs (adds/subs can produce values >= 2p,
        // which the single-fold reduction below cannot fully canonicalize).
        var work = new ulong[5];
        FeCopy(work, hIn);
        FeNormalize(work);

        ulong h0 = work[0], h1 = work[1], h2 = work[2], h3 = work[3], h4 = work[4];

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
        h4 &= Mask51;

        Store64(s, 0, h0 | (h1 << 51));
        Store64(s, 8, (h1 >> 13) | (h2 << 38));
        Store64(s, 16, (h2 >> 26) | (h3 << 25));
        Store64(s, 24, (h3 >> 39) | (h4 << 12));
    }

    // p-2, (p-5)/8 and (p-1)/4 as 32-byte big-endian exponents.
    private static readonly byte[] ExpPminus2 = BuildExp(0x7F, 0xEB);
    private static readonly byte[] ExpPminus5Div8 = BuildExp(0x0F, 0xFD);
    private static readonly byte[] ExpPminus1Div4 = BuildExp(0x1F, 0xFB);

    private static byte[] BuildExp(byte first, byte last)
    {
        var e = new byte[32];
        e[0] = first;
        for (int i = 1; i < 31; i++)
            e[i] = 0xFF;
        e[31] = last;
        return e;
    }

    /// <summary>r = z^exp (exp big-endian); MSB-first square-and-multiply.</summary>
    private static void FePow(ulong[] r, ulong[] z, byte[] exp)
    {
        var result = new ulong[5];
        bool started = false;
        for (int byteIdx = 0; byteIdx < exp.Length; byteIdx++)
        {
            int v = exp[byteIdx];
            for (int bit = 7; bit >= 0; bit--)
            {
                bool set = ((v >> bit) & 1) != 0;
                if (!started)
                {
                    if (!set)
                        continue;
                    FeCopy(result, z);
                    started = true;
                    continue;
                }
                FeSq(result, result);
                if (set)
                    FeMul(result, result, z);
            }
        }
        FeCopy(r, result);
    }

    private static void FeInvert(ulong[] r, ulong[] z) => FePow(r, z, ExpPminus2);

    /// <summary>r = z^((p-5)/8), the square-root helper.</summary>
    private static void FePow22523(ulong[] r, ulong[] z) => FePow(r, z, ExpPminus5Div8);

    /// <summary>Equality via canonical byte encoding.</summary>
    private static bool FeEqual(ulong[] a, ulong[] b)
    {
        var ab = new byte[32];
        var bb = new byte[32];
        FeToBytes(ab, a);
        FeToBytes(bb, b);
        for (int i = 0; i < 32; i++)
        {
            if (ab[i] != bb[i])
                return false;
        }
        return true;
    }

    // ==================== Curve constants (derived, not transcribed) ====================

    private static readonly ulong[] FE_D = ComputeD();
    private static readonly ulong[] FE_D2 = ComputeD2();
    private static readonly ulong[] FE_SQRTM1 = ComputeSqrtM1();

    /// <summary>The base point, decoded from its well-known encoding
    /// (y = 4/5 with sign bit 0; 0x58 followed by 0x66 x31).</summary>
    private static readonly GeP FE_BASE = ComputeBasePoint();

    private static ulong[] ComputeD()
    {
        var a = new ulong[5];
        a[0] = 121665;
        var b = new ulong[5];
        b[0] = 121666;
        var inv = new ulong[5];
        FeInvert(inv, b);
        var d = new ulong[5];
        FeMul(d, a, inv);
        FeNeg(d, d);
        return d;
    }

    private static ulong[] ComputeD2()
    {
        var d2 = new ulong[5];
        FeAdd(d2, FE_D, FE_D);
        return d2;
    }

    private static ulong[] ComputeSqrtM1()
    {
        // sqrt(-1) = 2^((p-1)/4).
        var two = new ulong[5];
        two[0] = 2;
        var r = new ulong[5];
        FePow(r, two, ExpPminus1Div4);
        return r;
    }

    private static GeP ComputeBasePoint()
    {
        var enc = new byte[32];
        enc[0] = 0x58;
        for (int i = 1; i < 32; i++)
            enc[i] = 0x66;
        var p = NewPoint();
        if (!DecodePoint(p, enc))
            throw new ArgumentException("base point setup failed");
        return p;
    }

    // ==================== Point arithmetic (extended coordinates) ====================

    private sealed class GeP
    {
        public readonly ulong[] X = new ulong[5];
        public readonly ulong[] Y = new ulong[5];
        public readonly ulong[] Z = new ulong[5];
        public readonly ulong[] T = new ulong[5];
    }

    private static GeP NewPoint()
    {
        var p = new GeP();
        p.Y[0] = 1;
        p.Z[0] = 1;
        return p;
    }

    private static void CopyPoint(GeP r, GeP a)
    {
        FeCopy(r.X, a.X);
        FeCopy(r.Y, a.Y);
        FeCopy(r.Z, a.Z);
        FeCopy(r.T, a.T);
    }

    /// <summary>Complete twisted-Edwards addition (a = -1, non-square d):
    /// works for doubling, identity and any mixed input.</summary>
    private static void AddPoint(GeP r, GeP p, GeP q)
    {
        var a = new ulong[5];
        var b = new ulong[5];
        var c = new ulong[5];
        var d = new ulong[5];
        var e = new ulong[5];
        var f = new ulong[5];
        var g = new ulong[5];
        var h = new ulong[5];
        var t = new ulong[5];

        FeSub(t, p.Y, p.X);
        FeSub(a, q.Y, q.X);
        FeMul(a, t, a);                 // A = (Y1-X1)(Y2-X2)

        FeAdd(t, p.Y, p.X);
        FeAdd(b, q.Y, q.X);
        FeMul(b, t, b);                 // B = (Y1+X1)(Y2+X2)

        FeMul(t, p.T, q.T);
        FeMul(c, t, FE_D2);             // C = T1 * 2d * T2

        FeMul(t, p.Z, q.Z);
        FeAdd(d, t, t);                 // D = 2 * Z1 * Z2

        FeSub(e, b, a);                 // E = B - A
        FeSub(f, d, c);                 // F = D - C
        FeAdd(g, d, c);                 // G = D + C
        FeAdd(h, b, a);                 // H = B + A

        FeMul(r.X, e, f);
        FeMul(r.Y, g, h);
        FeMul(r.T, e, h);
        FeMul(r.Z, f, g);
    }

    /// <summary>Constant-time select: r = bit ? a : b.</summary>
    private static void CselectPoint(GeP r, GeP a, GeP b, ulong bit)
    {
        ulong mask = 0UL - bit;
        ulong nmask = ~mask;
        for (int i = 0; i < 5; i++)
        {
            r.X[i] = (a.X[i] & mask) | (b.X[i] & nmask);
            r.Y[i] = (a.Y[i] & mask) | (b.Y[i] & nmask);
            r.Z[i] = (a.Z[i] & mask) | (b.Z[i] & nmask);
            r.T[i] = (a.T[i] & mask) | (b.T[i] & nmask);
        }
    }

    /// <summary>Scalar multiplication r = scalar * p (double-and-add-always,
    /// constant-time in the scalar).</summary>
    private static void ScalarMult(GeP r, byte[] scalar, GeP p)
    {
        var q = NewPoint();      // identity (0, 1, 1, 0)
        var t = NewPoint();

        for (int bit = 255; bit >= 0; bit--)
        {
            AddPoint(t, q, q);           // t = 2q
            CopyPoint(q, t);
            AddPoint(t, q, p);           // t = q + p
            ulong b = (ulong)((scalar[bit >> 3] >> (bit & 7)) & 1);
            CselectPoint(q, t, q, b);    // q = b ? q+p : q
        }
        CopyPoint(r, q);
    }

    /// <summary>Encode a point: y with the low bit of x in bit 255.</summary>
    private static void EncodePoint(byte[] s, GeP p)
    {
        var zi = new ulong[5];
        var x = new ulong[5];
        var y = new ulong[5];
        FeInvert(zi, p.Z);
        FeMul(x, p.X, zi);
        FeMul(y, p.Y, zi);
        FeToBytes(s, y);
        var xb = new byte[32];
        FeToBytes(xb, x);
        s[31] |= (byte)((xb[0] & 1) << 7);
    }

    /// <summary>Decode a point; returns false when the encoding is not on
    /// the curve.</summary>
    private static bool DecodePoint(GeP r, byte[] s)
    {
        var y = new ulong[5];
        FeFromBytes(y, s);

        var one = new ulong[5];
        one[0] = 1;
        var u = new ulong[5];
        var v = new ulong[5];
        FeSq(u, y);
        FeMul(v, u, FE_D);
        FeSub(u, u, one);               // u = y^2 - 1
        FeAdd(v, v, one);               // v = d*y^2 + 1

        var v3 = new ulong[5];
        var x = new ulong[5];
        FeSq(v3, v);
        FeMul(v3, v3, v);               // v3 = v^3
        FeSq(x, v3);
        FeMul(x, x, v);
        FeMul(x, x, u);                 // x = u*v^7
        FePow22523(x, x);
        FeMul(x, x, v3);
        FeMul(x, x, u);                 // x = u*v^3*(u*v^7)^((p-5)/8)

        var vxx = new ulong[5];
        FeSq(vxx, x);
        FeMul(vxx, vxx, v);             // vxx = x^2 * v

        var check = new ulong[5];
        FeSub(check, vxx, u);
        if (!IsZeroFe(check))
        {
            FeAdd(check, vxx, u);
            if (!IsZeroFe(check))
                return false;           // not on curve
            FeMul(x, x, FE_SQRTM1);     // x was a root of -u/v; fix with sqrt(-1)
        }

        // Apply the sign bit.
        var xb = new byte[32];
        FeToBytes(xb, x);
        int sign = (s[31] >> 7) & 1;
        if ((xb[0] & 1) != sign)
            FeNeg(x, x);

        FeCopy(r.X, x);
        FeCopy(r.Y, y);
        r.Z[0] = 1;
        r.Z[1] = 0; r.Z[2] = 0; r.Z[3] = 0; r.Z[4] = 0;
        FeMul(r.T, x, y);
        return true;
    }

    private static bool IsZeroFe(ulong[] a)
    {
        var ab = new byte[32];
        FeToBytes(ab, a);
        for (int i = 0; i < 32; i++)
        {
            if (ab[i] != 0)
                return false;
        }
        return true;
    }

    // ==================== Scalar arithmetic mod L ====================

    // L = 2^252 + 27742317777372353535851937790883648493 (little-endian).
    private static readonly byte[] L32 =
    {
        0xED, 0xD3, 0xF5, 0x5C, 0x1A, 0x63, 0x12, 0x58,
        0xD6, 0x9C, 0xF7, 0xA2, 0xDE, 0xF9, 0xDE, 0x14,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10,
    };

    /// <summary>True when the 32-byte little-endian value is >= L.</summary>
    private static bool GeL(byte[] v)
    {
        var l33 = new byte[33];
        for (int i = 0; i < 32; i++)
            l33[i] = L32[i];
        return Cmp33(v /*padded*/, l33) >= 0;
    }

    private static int Cmp33(byte[] v, byte[] l33)
    {
        for (int i = 32; i >= 0; i--)
        {
            byte a = i < v.Length ? v[i] : (byte)0;
            byte b = l33[i];
            if (a != b)
                return a > b ? 1 : -1;
        }
        return 0;
    }

    /// <summary>Reduce a little-endian value (up to 65 bytes) mod L into r.</summary>
    private static void ReduceModL(byte[] r, byte[] wide)
    {
        var rem = new byte[33];
        var l33 = new byte[33];
        for (int i = 0; i < 32; i++)
            l33[i] = L32[i];

        int totalBits = wide.Length * 8;
        for (int bit = totalBits - 1; bit >= 0; bit--)
        {
            // rem = rem * 2 + next bit
            int carry = (wide[bit >> 3] >> (bit & 7)) & 1;
            for (int i = 0; i < 33; i++)
            {
                int v = (rem[i] << 1) | carry;
                rem[i] = (byte)v;
                carry = v >> 8;
            }

            // if rem >= L: rem -= L
            if (Cmp33(rem, l33) >= 0)
            {
                int borrow = 0;
                for (int i = 0; i < 33; i++)
                {
                    int v = rem[i] - l33[i] - borrow;
                    if (v < 0)
                    {
                        v += 256;
                        borrow = 1;
                    }
                    else
                    {
                        borrow = 0;
                    }
                    rem[i] = (byte)v;
                }
            }
        }

        for (int i = 0; i < 32; i++)
            r[i] = rem[i];
    }

    /// <summary>r = (a*b + c) mod L for 32-byte little-endian inputs.</summary>
    private static void MulAddModL(byte[] r, byte[] a, byte[] b, byte[] c)
    {
        var wide = new byte[65];

        // Schoolbook 32x32-byte multiply.
        for (int i = 0; i < 32; i++)
        {
            int carry = 0;
            int ai = a[i];
            for (int j = 0; j < 32; j++)
            {
                int t = wide[i + j] + ai * b[j] + carry;
                wide[i + j] = (byte)t;
                carry = t >> 8;
            }
            wide[i + 32] = (byte)carry;
        }

        // Add c.
        int carry2 = 0;
        for (int i = 0; i < 32; i++)
        {
            int t = wide[i] + c[i] + carry2;
            wide[i] = (byte)t;
            carry2 = t >> 8;
        }
        for (int i = 32; i < 65 && carry2 != 0; i++)
        {
            int t = wide[i] + carry2;
            wide[i] = (byte)t;
            carry2 = t >> 8;
        }

        ReduceModL(r, wide);
    }

    // ==================== Public API ====================

    /// <summary>Derive the 32-byte public key from a 32-byte seed.</summary>
    public static byte[] PublicKeyFromSeed(byte[] seed)
    {
        if (seed == null || seed.Length != SeedSize)
            throw new ArgumentException("seed must be 32 bytes");

        var h = new Sha512();
        h.Update(seed, 0, 32);
        var hh = h.Finish();

        var s = new byte[32];
        for (int i = 0; i < 32; i++)
            s[i] = hh[i];
        ClampScalar(s);

        var a = NewPoint();
        ScalarMult(a, s, FE_BASE);
        var pub = new byte[32];
        EncodePoint(pub, a);
        return pub;
    }

    private static void ClampScalar(byte[] s)
    {
        s[0] &= 248;
        s[31] &= 127;
        s[31] |= 64;
    }

    /// <summary>Sign a message with a 32-byte seed; returns 64 bytes.</summary>
    public static byte[] Sign(byte[] seed, byte[] message)
        => Sign(seed, message, 0, message == null ? 0 : message.Length);

    /// <summary>Sign a message slice with a 32-byte seed; returns 64 bytes.</summary>
    public static byte[] Sign(byte[] seed, byte[] message, int offset, int length)
    {
        if (seed == null || seed.Length != SeedSize)
            throw new ArgumentException("seed must be 32 bytes");

        var h = new Sha512();
        h.Update(seed, 0, 32);
        var hh = h.Finish();

        var s = new byte[32];
        var prefix = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            s[i] = hh[i];
            prefix[i] = hh[32 + i];
        }
        ClampScalar(s);

        // A = s * B
        var aPoint = NewPoint();
        ScalarMult(aPoint, s, FE_BASE);
        var pub = new byte[32];
        EncodePoint(pub, aPoint);

        // r = H(prefix || M) mod L; R = r * B
        var hr = new Sha512();
        hr.Update(prefix, 0, 32);
        hr.Update(message, offset, length);
        var hw = hr.Finish();
        var r = new byte[32];
        ReduceModL(r, hw);

        var rPoint = NewPoint();
        ScalarMult(rPoint, r, FE_BASE);
        var rEnc = new byte[32];
        EncodePoint(rEnc, rPoint);

        // k = H(R || A || M) mod L; S = (k*s + r) mod L
        var hk = new Sha512();
        hk.Update(rEnc, 0, 32);
        hk.Update(pub, 0, 32);
        hk.Update(message, offset, length);
        var kw = hk.Finish();
        var k = new byte[32];
        ReduceModL(k, kw);

        var sOut = new byte[32];
        MulAddModL(sOut, k, s, r);

        var sig = new byte[64];
        for (int i = 0; i < 32; i++)
        {
            sig[i] = rEnc[i];
            sig[32 + i] = sOut[i];
        }
        return sig;
    }

    /// <summary>Verify a 64-byte signature over a message slice.</summary>
    public static bool Verify(byte[] publicKey, byte[] message, int offset, int length, byte[] signature)
    {
        if (publicKey == null || publicKey.Length != 32)
            return false;
        if (signature == null || signature.Length != 64)
            return false;

        // S must be canonical (< L).
        var sBytes = new byte[32];
        for (int i = 0; i < 32; i++)
            sBytes[i] = signature[32 + i];
        if (GeL(sBytes))
            return false;

        // Decode A and R.
        var aKey = new byte[32];
        for (int i = 0; i < 32; i++)
            aKey[i] = publicKey[i];
        var rEnc = new byte[32];
        for (int i = 0; i < 32; i++)
            rEnc[i] = signature[i];

        var aPoint = NewPoint();
        if (!DecodePoint(aPoint, aKey))
            return false;
        var rPoint = NewPoint();
        if (!DecodePoint(rPoint, rEnc))
            return false;

        // k = H(R || A || M) mod L
        var hk = new Sha512();
        hk.Update(rEnc, 0, 32);
        hk.Update(aKey, 0, 32);
        hk.Update(message, offset, length);
        var kw = hk.Finish();
        var k = new byte[32];
        ReduceModL(k, kw);

        // Check encode(S*B) == encode(R + k*A).
        var sb = NewPoint();
        ScalarMult(sb, sBytes, FE_BASE);
        var kA = NewPoint();
        ScalarMult(kA, k, aPoint);
        var sum = NewPoint();
        AddPoint(sum, rPoint, kA);

        var left = new byte[32];
        var right = new byte[32];
        EncodePoint(left, sb);
        EncodePoint(right, sum);

        int diff = 0;
        for (int i = 0; i < 32; i++)
            diff |= left[i] ^ right[i];
        return diff == 0;
    }

    /// <summary>Verify a signature over a whole message.</summary>
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        => Verify(publicKey, message, 0, message == null ? 0 : message.Length, signature);

    /// <summary>Test hook: attempt to decode a point encoding.</summary>
    internal static bool DebugDecode(byte[] enc)
    {
        var p = NewPoint();
        return DecodePoint(p, enc);
    }

    /// <summary>Test hook: dump decode intermediates as hex.</summary>
    internal static string DebugDecodeDetail(byte[] enc)
    {
        var y = new ulong[5];
        FeFromBytes(y, enc);
        var one = new ulong[5];
        one[0] = 1;
        var u = new ulong[5];
        var v = new ulong[5];
        FeSq(u, y);
        FeMul(v, u, FE_D);
        FeSub(u, u, one);
        FeAdd(v, v, one);
        var v3 = new ulong[5];
        var x = new ulong[5];
        FeSq(v3, v);
        FeMul(v3, v3, v);
        FeSq(x, v3);
        FeMul(x, x, v);
        FeMul(x, x, u);
        var t = new ulong[5];
        FeCopy(t, x);
        FePow22523(x, x);
        FeMul(x, x, v3);
        FeMul(x, x, u);
        var vxx = new ulong[5];
        FeSq(vxx, x);
        FeMul(vxx, vxx, v);
        var minus = new ulong[5];
        FeSub(minus, vxx, u);
        var plus = new ulong[5];
        FeAdd(plus, vxx, u);
        return "y=" + DbgHex(y) + " u=" + DbgHex(u) + " v=" + DbgHex(v) +
               " t=" + DbgHex(t) + " x=" + DbgHex(x) +
               " vxx-u=" + DbgHex(minus) + " vxx+u=" + DbgHex(plus);
    }

    private static string DbgHex(ulong[] a)
    {
        var b = new byte[32];
        FeToBytes(b, a);
        string s = "";
        string[] digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "a", "b", "c", "d", "e", "f" };
        for (int i = 31; i >= 0; i--)
        {
            s += digits[b[i] >> 4];
            s += digits[b[i] & 15];
        }
        return s;
    }

    /// <summary>Test hook: the S-canonical check used by Verify.</summary>
    internal static bool DebugGeL(byte[] s32) => GeL(s32);
}
