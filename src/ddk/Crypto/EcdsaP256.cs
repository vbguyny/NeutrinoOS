// NeutrinoOS DDK - ECDSA over NIST P-256 (server-side signatures)
//
// Why this exists: the TLS 1.3 server signs with an Ed25519 certificate,
// but Windows Schannel clients do not offer ed25519 in
// signature_algorithms - they need ecdsa_secp256r1_sha256 (0x0403) or
// RSA. This module adds the smallest Schannel-accepted scheme: ECDSA
// P-256 signatures (SHA-256), used by Tls13Connection when the client
// does not offer ed25519, and by the P-256 certificate builder in
// X509.cs.
//
// Implementation notes (Tier-0 JIT safe):
//   - 4x64-bit limbs, little-endian; no System.Numerics / UInt128 -
//     only 64x64->128 via Mul64 (same style as Ed25519.cs).
//   - Modular multiplication is Montgomery CIOS, parameterized by
//     modulus + -m^-1 mod 2^64 (constants precomputed in
//     build/tmp-p256-constants.py; n0inv_p is 1 because p ends in
//     ...FFFF...FF).
//   - Curve math in Jacobian coordinates on the Montgomery domain;
//     inverses via Fermat (a^(m-2)). a = -3 doubling formulas.
//   - Deterministic test hook: SignWithK takes the nonce explicitly so
//     the JITTest vector (also computed in the python scratch file) can
//     pin r/s/DER bytes exactly; Sign() loops a CSPRNG nonce.
//
// Test vector (build/tmp-p256-constants.py):
//   d    = 0x519B423D...(see VECTORS below)
//   k    = 0x7A1A0F0A...
//   sha256("NeutrinoOS ECDSA P-256 test vector") -> DER signature
//   pinned in JITTest.EcdsaP256Tests.

using System;

namespace NeutrinoOS.DDK.Crypto;

/// <summary>Managed ECDSA over P-256 (see file header).</summary>
public static class EcdsaP256
{
    /// <summary>Private key size (32-byte big-endian scalar).</summary>
    public const int PrivateKeySize = 32;

    /// <summary>Uncompressed public key size (0x04 || X || Y, 65 bytes).</summary>
    public const int PublicKeySize = 65;

    // ==================== Constants ====================

    // p = 2^256 - 2^224 + 2^192 + 2^96 - 1 (limbs little-endian).
    private static readonly ulong[] P = {
        0xFFFFFFFFFFFFFFFFUL, 0x00000000FFFFFFFFUL,
        0x0000000000000000UL, 0xFFFFFFFF00000001UL };

    // n = group order (limbs little-endian).
    private static readonly ulong[] N = {
        0xF3B9CAC2FC632551UL, 0xBCE6FAADA7179E84UL,
        0xFFFFFFFFFFFFFFFFUL, 0xFFFFFFFF00000000UL };

    // -p^-1 mod 2^64 == 1; -n^-1 mod 2^64 (python scratch).
    private const ulong N0P = 0x0000000000000001UL;
    private const ulong N0N = 0xCCD1C8AAEE00BC4FUL;

    // R^2 mod p and mod n (for ToMont), limbs little-endian.
    private static readonly ulong[] R2P = {
        0x0000000000000003UL, 0xFFFFFFFBFFFFFFFFUL,
        0xFFFFFFFFFFFFFFFEUL, 0x00000004FFFFFFFDUL };
    private static readonly ulong[] R2N = {
        0x83244C95BE79EEA2UL, 0x4699799C49BD6FA6UL,
        0x2845B2392B6BEC59UL, 0x66E12D94F3D95620UL };

    // Base point G (limbs little-endian; Montgomery conversions happen
    // at runtime via ToMont).
    private static readonly ulong[] GX = {
        0xF4A13945D898C296UL, 0x77037D812DEB33A0UL,
        0xF8BCE6E563A440F2UL, 0x6B17D1F2E12C4247UL };
    private static readonly ulong[] GY = {
        0xCBB6406837BF51F5UL, 0x2BCE33576B315ECEUL,
        0x8EE7EB4A7C0F9E16UL, 0x4FE342E2FE1A7F9BUL };

    // p-2 and n-2 (Fermat exponents), little-endian.
    private static readonly ulong[] PM2 = {
        0xFFFFFFFFFFFFFFFDUL, 0x00000000FFFFFFFFUL,
        0x0000000000000000UL, 0xFFFFFFFF00000001UL };
    private static readonly ulong[] NM2 = {
        0xF3B9CAC2FC63254FUL, 0xBCE6FAADA7179E84UL,
        0xFFFFFFFFFFFFFFFFUL, 0xFFFFFFFF00000000UL };

    // n/2 (low-s threshold), little-endian.
    private static readonly ulong[] NHalf = {
        0x79DCE5617E3192A8UL, 0xDE737D56D38BCF42UL,
        0x7FFFFFFFFFFFFFFFUL, 0x7FFFFFFF80000000UL };

    // 2^256 - p and 2^256 - n (the add-carry correction), little-endian.
    private static readonly ulong[] WrapP = {
        0x0000000000000001UL, 0xFFFFFFFF00000000UL,
        0xFFFFFFFFFFFFFFFFUL, 0x00000000FFFFFFFEUL };
    private static readonly ulong[] WrapN = {
        0x0C46353D039CDAAFUL, 0x4319055258E8617BUL,
        0x0000000000000000UL, 0x00000000FFFFFFFFUL };

    private static readonly ulong[] One = { 1UL, 0UL, 0UL, 0UL };

    // ==================== 64x64 -> 128 helpers ====================

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

    /// <summary>
    /// t = t + a*b + carry (t and carry in/out). Returns the new low
    /// limb; <paramref name="carry"/> becomes the new high limb.
    /// The 128-bit total is bounded by 2^128-1, so hi cannot wrap.
    /// </summary>
    private static ulong AddMulCarry(ulong a, ulong b, ulong t, ref ulong carry)
    {
        Mul64(a, b, out ulong hi, out ulong lo);
        ulong s = t + lo;
        ulong s2 = s + carry;
        ulong ov = (s < lo ? 1UL : 0UL) + (s2 < s ? 1UL : 0UL);
        carry = hi + ov;
        return s2;
    }

    // ==================== limb utilities ====================

    private static void Copy4(ulong[] r, ulong[] a)
    {
        r[0] = a[0];
        r[1] = a[1];
        r[2] = a[2];
        r[3] = a[3];
    }

    private static bool IsZero4(ulong[] a)
    {
        return a[0] == 0 && a[1] == 0 && a[2] == 0 && a[3] == 0;
    }

    /// <summary>Compare a >= b (4 limbs).</summary>
    private static bool Gte(ulong[] a, ulong[] b)
    {
        for (int i = 3; i >= 0; i--)
        {
            if (a[i] > b[i])
                return true;
            if (a[i] < b[i])
                return false;
        }
        return true;
    }

    /// <summary>a -= b (4 limbs; caller guarantees a >= b).</summary>
    private static void Sub4InPlace(ulong[] a, ulong[] b)
    {
        ulong borrow = 0;
        for (int i = 0; i < 4; i++)
        {
            ulong bi = b[i] + borrow;
            ulong carried = bi < b[i] ? 1UL : 0UL;   // b[i]+borrow wrapped
            ulong r = a[i] - bi;
            borrow = (r > a[i] || carried != 0) ? 1UL : 0UL;
            a[i] = r;
        }
    }

    // ==================== Montgomery arithmetic ====================

    /// <summary>
    /// r = a * b * R^-1 mod m (CIOS). Operands &lt; m. m0inv is
    /// -m^-1 mod 2^64. r may alias a or b only through separate temps.
    /// </summary>
    private static void MontMul(ulong[] r, ulong[] a, ulong[] b, ulong[] m, ulong m0inv)
    {
        // 6-limb accumulator so carries never wrap.
        ulong t0 = 0, t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0;

        for (int i = 0; i < 4; i++)
        {
            ulong bi = b[i];
            ulong c = 0;
            t0 = AddMulCarry(a[0], bi, t0, ref c);
            t1 = AddMulCarry(a[1], bi, t1, ref c);
            t2 = AddMulCarry(a[2], bi, t2, ref c);
            t3 = AddMulCarry(a[3], bi, t3, ref c);
            ulong s4 = t4 + c;
            t5 += s4 < t4 ? 1UL : 0UL;
            t4 = s4;

            ulong u = t0 * m0inv;
            c = 0;
            t0 = AddMulCarry(m[0], u, t0, ref c);
            t1 = AddMulCarry(m[1], u, t1, ref c);
            t2 = AddMulCarry(m[2], u, t2, ref c);
            t3 = AddMulCarry(m[3], u, t3, ref c);
            s4 = t4 + c;
            t5 += s4 < t4 ? 1UL : 0UL;
            t4 = s4;

            // Shift the accumulator down one limb (t0 is 0 by design).
            t0 = t1;
            t1 = t2;
            t2 = t3;
            t3 = t4;
            t4 = t5;
            t5 = 0;
        }

        // Conditionally subtract m while the value >= m (at most twice).
        var v = new ulong[] { t0, t1, t2, t3 };
        while (t4 != 0 || t5 != 0 || Gte(v, m))
        {
            // v = (t4:t5:t3:t2:t1:t0) - m
            ulong borrow = 0;
            for (int j = 0; j < 4; j++)
            {
                ulong mj = m[j] + borrow;
                ulong carried = mj < m[j] ? 1UL : 0UL;
                ulong rj = v[j] - mj;
                borrow = (rj > v[j] || carried != 0) ? 1UL : 0UL;
                v[j] = rj;
            }
            // Borrow propagates through t4/t5 (the top limbs).
            ulong top = t4 - borrow;
            t5 -= top > t4 ? 1UL : 0UL;
            t4 = top;
        }

        r[0] = v[0];
        r[1] = v[1];
        r[2] = v[2];
        r[3] = v[3];
    }

    /// <summary>r = a * R mod m (enter the Montgomery domain).</summary>
    private static void ToMont(ulong[] r, ulong[] a, ulong[] m, ulong m0inv, ulong[] r2)
    {
        MontMul(r, a, r2, m, m0inv);
    }

    /// <summary>r = a * R^-1 mod m (leave the Montgomery domain).</summary>
    private static void FromMont(ulong[] r, ulong[] a, ulong[] m, ulong m0inv)
    {
        MontMul(r, a, One, m, m0inv);
    }

    /// <summary>r = a^e mod m, Montgomery domain in and out.</summary>
    private static void MontPow(ulong[] r, ulong[] a, ulong[] e, ulong[] m, ulong m0inv, ulong[] r2)
    {
        // Start from Montgomery 1 (R mod m): ToMont(1).
        var res = new ulong[4];
        ToMont(res, One, m, m0inv, r2);

        var basev = new ulong[4];
        Copy4(basev, a);
        var tmp = new ulong[4];

        for (int bit = 255; bit >= 0; bit--)
        {
            MontMul(tmp, res, res, m, m0inv);
            Copy4(res, tmp);
            if (GetBit(e, bit))
            {
                MontMul(tmp, res, basev, m, m0inv);
                Copy4(res, tmp);
            }
        }
        Copy4(r, res);
    }

    private static bool GetBit(ulong[] limbs, int bit)
    {
        return ((limbs[bit >> 6] >> (bit & 63)) & 1UL) != 0UL;
    }

    // ==================== field ops (mod p, Montgomery domain) ====================

    private static void AddP(ulong[] r, ulong[] a, ulong[] b)
    {
        ulong carry = 0;
        for (int i = 0; i < 4; i++)
        {
            ulong s = a[i] + b[i];
            ulong c1 = s < a[i] ? 1UL : 0UL;
            ulong s2 = s + carry;
            carry = c1 + (s2 < s ? 1UL : 0UL);
            r[i] = s2;
        }
        if (carry != 0)
        {
            // The sum overflowed 256 bits: result = r + (2^256 - p).
            // With inputs < p, r < p holds, so no carry can come out.
            AddWrap(r, WrapP);
        }
        else if (Gte(r, P))
        {
            Sub4InPlace(r, P);
        }
    }

    /// <summary>r += k limb-wise (caller guarantees no carry out).</summary>
    private static void AddWrap(ulong[] r, ulong[] k)
    {
        ulong carry = 0;
        for (int i = 0; i < 4; i++)
        {
            ulong s = r[i] + k[i];
            ulong c1 = s < r[i] ? 1UL : 0UL;
            ulong s2 = s + carry;
            carry = c1 + (s2 < s ? 1UL : 0UL);
            r[i] = s2;
        }
    }

    private static void SubP(ulong[] r, ulong[] a, ulong[] b)
    {
        ulong borrow = 0;
        for (int i = 0; i < 4; i++)
        {
            ulong bi = b[i] + borrow;
            ulong carried = bi < b[i] ? 1UL : 0UL;
            ulong rj = a[i] - bi;
            borrow = (rj > a[i] || carried != 0) ? 1UL : 0UL;
            r[i] = rj;
        }
        if (borrow != 0)
        {
            ulong c = 0;
            for (int i = 0; i < 4; i++)
            {
                ulong s = r[i] + P[i];
                ulong c1 = s < r[i] ? 1UL : 0UL;
                ulong s2 = s + c;
                c = c1 + (s2 < s ? 1UL : 0UL);
                r[i] = s2;
            }
        }
    }

    private static ulong[] MulP(ulong[] a, ulong[] b)
    {
        var r = new ulong[4];
        MontMul(r, a, b, P, N0P);
        return r;
    }

    private static ulong[] SqrP(ulong[] a)
    {
        return MulP(a, a);
    }

    private static ulong[] InvP(ulong[] a)
    {
        var r = new ulong[4];
        MontPow(r, a, PM2, P, N0P, R2P);
        return r;
    }

    // ==================== Jacobian curve ops (Montgomery domain) ====================
    //
    // Point represented as three ulong[4] arrays X, Y, Z; infinity is Z = 0.

    private static void PointDouble(ulong[] x, ulong[] y, ulong[] z)
    {
        if (IsZero4(z))
            return;
        // dbl-2001-b (a = -3):
        //   delta = Z^2; gamma = Y^2; beta = X*gamma
        //   alpha = 3*(X-delta)*(X+delta)
        //   X3 = alpha^2 - 8*beta
        //   Z3 = (Y+Z)^2 - gamma - delta
        //   Y3 = alpha*(4*beta - X3) - 8*gamma^2
        ulong[] delta = SqrP(z);
        ulong[] gamma = SqrP(y);
        ulong[] beta = MulP(x, gamma);

        ulong[] t1 = new ulong[4];
        ulong[] t2 = new ulong[4];
        SubP(t1, x, delta);            // X - delta
        AddP(t2, x, delta);            // X + delta
        ulong[] alpha = MulP(t1, t2);
        AddP(t1, alpha, alpha);        // 2*alpha
        AddP(alpha, t1, alpha);        // 3*alpha

        ulong[] x3 = SqrP(alpha);      // alpha^2
        ulong[] beta8 = new ulong[4];
        AddP(beta8, beta, beta);       // 2b
        AddP(beta8, beta8, beta8);     // 4b
        AddP(beta8, beta8, beta8);     // 8b
        SubP(x3, x3, beta8);

        ulong[] z3 = new ulong[4];
        AddP(t1, y, z);                // Y + Z
        z3 = SqrP(t1);
        SubP(z3, z3, gamma);
        SubP(z3, z3, delta);

        ulong[] y3 = new ulong[4];
        AddP(t1, beta, beta);
        AddP(t1, t1, t1);              // 4b
        SubP(t1, t1, x3);              // 4b - X3
        y3 = MulP(alpha, t1);
        ulong[] g2 = SqrP(gamma);      // gamma^2
        AddP(t2, g2, g2);              // 2g2
        AddP(t2, t2, t2);              // 4g2
        AddP(t2, t2, t2);              // 8g2
        SubP(y3, y3, t2);

        Copy4(x, x3);
        Copy4(y, y3);
        Copy4(z, z3);
    }

    /// <summary>p1 += p2 (Jacobian). Handles infinity and doubling.</summary>
    private static void PointAdd(ulong[] x1, ulong[] y1, ulong[] z1,
        ulong[] x2, ulong[] y2, ulong[] z2)
    {
        if (IsZero4(z2))
            return;
        if (IsZero4(z1))
        {
            Copy4(x1, x2);
            Copy4(y1, y2);
            Copy4(z1, z2);
            return;
        }

        // add-2007-bl:
        ulong[] z1z1 = SqrP(z1);
        ulong[] z2z2 = SqrP(z2);
        ulong[] u1 = MulP(x1, z2z2);
        ulong[] u2 = MulP(x2, z1z1);
        ulong[] s1 = MulP(y1, z2);     // Y1*Z2
        s1 = MulP(s1, z2z2);           // *Z2^2 -> S1
        ulong[] s2 = MulP(y2, z1);
        s2 = MulP(s2, z1z1);           // S2

        ulong[] h = new ulong[4];
        SubP(h, u2, u1);               // H = U2-U1
        if (IsZero4(h))
        {
            ulong[] sdiff = new ulong[4];
            SubP(sdiff, s2, s1);
            if (IsZero4(sdiff))
            {
                PointDouble(x1, y1, z1);
            }
            else
            {
                // Opposite points -> infinity.
                z1[0] = 0; z1[1] = 0; z1[2] = 0; z1[3] = 0;
                x1[0] = 0; x1[1] = 0; x1[2] = 0; x1[3] = 0;
                y1[0] = 0; y1[1] = 0; y1[2] = 0; y1[3] = 0;
            }
            return;
        }

        ulong[] rr = new ulong[4];
        SubP(rr, s2, s1);
        AddP(rr, rr, rr);              // r = 2*(S2-S1)

        ulong[] h2 = SqrP(h);
        ulong[] i = new ulong[4];
        AddP(i, h2, h2);               // 2H^2
        AddP(i, i, i);                 // I = 4H^2
        ulong[] j = MulP(h, i);        // J = H*I
        ulong[] v = MulP(u1, i);       // V = U1*I

        ulong[] x3 = SqrP(rr);
        SubP(x3, x3, j);
        ulong[] v2 = new ulong[4];
        AddP(v2, v, v);
        SubP(x3, x3, v2);              // X3 = r^2 - J - 2V

        ulong[] y3 = new ulong[4];
        SubP(y3, v, x3);               // V - X3
        y3 = MulP(rr, y3);
        ulong[] sj = MulP(s1, j);
        AddP(sj, sj, sj);              // 2*S1*J
        SubP(y3, y3, sj);

        ulong[] z3 = new ulong[4];
        AddP(z3, z1, z2);
        z3 = SqrP(z3);
        SubP(z3, z3, z1z1);
        SubP(z3, z3, z2z2);
        z3 = MulP(z3, h);              // Z3 = ((Z1+Z2)^2 - Z1Z1 - Z2Z2)*H

        Copy4(x1, x3);
        Copy4(y1, y3);
        Copy4(z1, z3);
    }

    /// <summary>
    /// Scalar multiplication k*P (scalar 32-byte big-endian, plain bits).
    /// Result is left as a Jacobian point in the caller's arrays
    /// (Montgomery domain).
    /// </summary>
    private static void ScalarMult(ulong[] rx, ulong[] ry, ulong[] rz,
        ulong[] px, ulong[] py, ulong[] pz, byte[] scalar)
    {
        // Infinity.
        rx[0] = rx[1] = rx[2] = rx[3] = 0;
        ry[0] = ry[1] = ry[2] = ry[3] = 0;
        rz[0] = rz[1] = rz[2] = rz[3] = 0;

        for (int bit = 0; bit < 256; bit++)
        {
            PointDouble(rx, ry, rz);
            int byteIndex = bit >> 3;
            int bitIndex = 7 - (bit & 7);
            if (((scalar[byteIndex] >> bitIndex) & 1) != 0)
                PointAdd(rx, ry, rz, px, py, pz);
        }
    }

    /// <summary>x = X / Z^2 (Montgomery domain in and out).</summary>
    private static ulong[] AffineX(ulong[] x, ulong[] z)
    {
        ulong[] zInv = InvP(z);
        ulong[] zInv2 = SqrP(zInv);
        return MulP(x, zInv2);
    }

    // ==================== byte conversions ====================

    private static void FromBytesBE(ulong[] limbs, byte[] b, int off)
    {
        for (int i = 0; i < 4; i++)
        {
            ulong v = 0;
            int baseOff = off + (3 - i) * 8;
            for (int j = 0; j < 8; j++)
                v = (v << 8) | b[baseOff + j];
            limbs[i] = v;
        }
    }

    private static void ToBytesBE(byte[] outb, int off, ulong[] limbs)
    {
        for (int i = 0; i < 4; i++)
        {
            ulong v = limbs[i];
            int baseOff = off + (3 - i) * 8;
            for (int j = 0; j < 8; j++)
                outb[baseOff + j] = (byte)(v >> (56 - 8 * j));
        }
    }

    /// <summary>Reduce a 256-bit big-endian value mod n (single subtract).</summary>
    private static void ReduceModN(ulong[] limbs)
    {
        if (Gte(limbs, N))
            Sub4InPlace(limbs, N);
    }

    // ==================== public API ====================

    /// <summary>Public key for a private scalar: 0x04 || X || Y (65 bytes).</summary>
    public static byte[] PublicKeyFromPrivate(byte[] priv)
    {
        var gx = new ulong[4];
        var gy = new ulong[4];
        var gz = new ulong[4];
        ToMont(gx, GX, P, N0P, R2P);
        ToMont(gy, GY, P, N0P, R2P);
        gz[0] = 1; gz[1] = 0; gz[2] = 0; gz[3] = 0;
        ToMont(gz, gz, P, N0P, R2P);

        var rx = new ulong[4];
        var ry = new ulong[4];
        var rz = new ulong[4];
        ScalarMult(rx, ry, rz, gx, gy, gz, priv);

        // Affine: x = X/Z^2, y = Y/Z^3.
        var zInv = InvP(rz);
        var zInv2 = SqrP(zInv);
        var zInv3 = MulP(zInv2, zInv);
        var xa = MulP(rx, zInv2);
        var ya = MulP(ry, zInv3);

        var xp = new ulong[4];
        var yp = new ulong[4];
        FromMont(xp, xa, P, N0P);
        FromMont(yp, ya, P, N0P);

        var pub = new byte[PublicKeySize];
        pub[0] = 0x04;
        ToBytesBE(pub, 1, xp);
        ToBytesBE(pub, 33, yp);
        return pub;
    }

    /// <summary>
    /// ECDSA sign with an explicit nonce (deterministic; used by the
    /// JITTest vectors and internally by Sign). Returns the DER
    /// ECDSA-Sig-Value, or null when k yields r == 0 or s == 0.
    /// </summary>
    public static byte[] SignWithK(byte[] priv, byte[] digest, byte[] kBytes)
    {
        // e = digest mod n (plain domain).
        var e = new ulong[4];
        FromBytesBE(e, digest, 0);
        ReduceModN(e);

        // k (plain), reject zero.
        var k = new ulong[4];
        FromBytesBE(k, kBytes, 0);
        ReduceModN(k);
        if (IsZero4(k))
            return null;

        // r = (k*G).x mod n.
        var gx = new ulong[4];
        var gy = new ulong[4];
        var gz = new ulong[4];
        ToMont(gx, GX, P, N0P, R2P);
        ToMont(gy, GY, P, N0P, R2P);
        gz[0] = 1; gz[1] = 0; gz[2] = 0; gz[3] = 0;
        ToMont(gz, gz, P, N0P, R2P);

        var rx = new ulong[4];
        var ry = new ulong[4];
        var rz = new ulong[4];
        ScalarMult(rx, ry, rz, gx, gy, gz, kBytes);

        var xa = AffineX(rx, rz);
        var xp = new ulong[4];
        FromMont(xp, xa, P, N0P);

        var r = new ulong[4];
        Copy4(r, xp);
        ReduceModN(r);
        if (IsZero4(r))
            return null;

        // s = k^-1 * (e + r*d) mod n (Montgomery domain mod n).
        var d = new ulong[4];
        FromBytesBE(d, priv, 0);
        ReduceModN(d);

        var rd = new ulong[4];
        {
            var rM = new ulong[4];
            var dM = new ulong[4];
            ToMont(rM, r, N, N0N, R2N);
            ToMont(dM, d, N, N0N, R2N);
            MontMul(rd, rM, dM, N, N0N);   // r*d*R^-1 (domain)
        }
        // sum = (e + r*d) mod n, in domain.
        var sum = new ulong[4];
        {
            var eM = new ulong[4];
            ToMont(eM, e, N, N0N, R2N);
            // AddP-style add would use modulus p; add mod n explicitly.
            AddModN(sum, eM, rd);
        }
        var kinv = new ulong[4];
        {
            // Invert k mod n: plain -> domain -> pow(n-2) -> plain.
            var kM = new ulong[4];
            ToMont(kM, k, N, N0N, R2N);
            var kInvM = new ulong[4];
            MontPow(kInvM, kM, NM2, N, N0N, R2N);
            FromMont(kinv, kInvM, N, N0N);
        }
        var s = new ulong[4];
        {
            var kinvM = new ulong[4];
            ToMont(kinvM, kinv, N, N0N, R2N);
            var sM = new ulong[4];
            MontMul(sM, kinvM, sum, N, N0N);
            FromMont(s, sM, N, N0N);
        }
        if (IsZero4(s))
            return null;

        // Low-s normalization: s > n/2 -> s = n - s.
        if (!Gte(NHalf, s))
        {
            var tmp = new ulong[4];
            Sub4Raw(tmp, N, s);
            Copy4(s, tmp);
        }

        return EncodeDer(r, s);
    }

    /// <summary>r = a - b limb-wise (mod 2^256; borrow discarded).</summary>
    private static void Sub4Raw(ulong[] r, ulong[] a, ulong[] b)
    {
        ulong borrow = 0;
        for (int i = 0; i < 4; i++)
        {
            ulong bi = b[i] + borrow;
            ulong carried = bi < b[i] ? 1UL : 0UL;
            ulong rj = a[i] - bi;
            borrow = (rj > a[i] || carried != 0) ? 1UL : 0UL;
            r[i] = rj;
        }
    }

    private static void AddModN(ulong[] r, ulong[] a, ulong[] b)
    {
        ulong carry = 0;
        for (int i = 0; i < 4; i++)
        {
            ulong s = a[i] + b[i];
            ulong c1 = s < a[i] ? 1UL : 0UL;
            ulong s2 = s + carry;
            carry = c1 + (s2 < s ? 1UL : 0UL);
            r[i] = s2;
        }
        if (carry != 0)
        {
            AddWrap(r, WrapN);
        }
        else if (Gte(r, N))
        {
            Sub4InPlace(r, N);
        }
    }

    /// <summary>Sign with a fresh CSPRNG nonce (retries on r/s = 0).</summary>
    public static byte[] Sign(byte[] priv, byte[] digest)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            byte[] k = Csprng.GetBytes(32);
            byte[] sig = SignWithK(priv, digest, k);
            if (sig != null)
                return sig;
        }
        return null;
    }

    /// <summary>Random private scalar in [1, n-1] from the CSPRNG.</summary>
    public static byte[] GeneratePrivateKey()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            byte[] b = Csprng.GetBytes(32);
            var limbs = new ulong[4];
            FromBytesBE(limbs, b, 0);
            ReduceModN(limbs);
            if (!IsZero4(limbs))
            {
                var outb = new byte[32];
                ToBytesBE(outb, 0, limbs);
                return outb;
            }
        }
        return null;
    }

    /// <summary>Verify a DER-encoded ECDSA signature against a public key.</summary>
    public static bool Verify(byte[] pub, byte[] digest, byte[] derSig)
    {
        if (pub == null || pub.Length != PublicKeySize || pub[0] != 0x04)
            return false;

        var r = new ulong[4];
        var s = new ulong[4];
        if (!DecodeDer(derSig, r, s))
            return false;
        if (IsZero4(r) || IsZero4(s))
            return false;

        // e mod n.
        var e = new ulong[4];
        FromBytesBE(e, digest, 0);
        ReduceModN(e);

        // w = s^-1, u1 = e*w, u2 = r*w (all mod n).
        var w = new ulong[4];
        {
            var sM = new ulong[4];
            ToMont(sM, s, N, N0N, R2N);
            var wM = new ulong[4];
            MontPow(wM, sM, NM2, N, N0N, R2N);
            FromMont(w, wM, N, N0N);
        }
        var u1 = new ulong[4];
        var u2 = new ulong[4];
        {
            var wM = new ulong[4];
            var eM = new ulong[4];
            var rM = new ulong[4];
            ToMont(wM, w, N, N0N, R2N);
            ToMont(eM, e, N, N0N, R2N);
            ToMont(rM, r, N, N0N, R2N);
            var t1 = new ulong[4];
            var t2 = new ulong[4];
            MontMul(t1, eM, wM, N, N0N);
            MontMul(t2, rM, wM, N, N0N);
            FromMont(u1, t1, N, N0N);
            FromMont(u2, t2, N, N0N);
        }

        // R = u1*G + u2*Q.
        var gx = new ulong[4];
        var gy = new ulong[4];
        var gz = new ulong[4];
        ToMont(gx, GX, P, N0P, R2P);
        ToMont(gy, GY, P, N0P, R2P);
        gz[0] = 1; gz[1] = 0; gz[2] = 0; gz[3] = 0;
        ToMont(gz, gz, P, N0P, R2P);

        var qx = new ulong[4];
        var qy = new ulong[4];
        var qz = new ulong[4];
        var qxPlain = new ulong[4];
        var qyPlain = new ulong[4];
        FromBytesBE(qxPlain, pub, 1);
        FromBytesBE(qyPlain, pub, 33);
        ToMont(qx, qxPlain, P, N0P, R2P);
        ToMont(qy, qyPlain, P, N0P, R2P);
        qz[0] = 1; qz[1] = 0; qz[2] = 0; qz[3] = 0;
        ToMont(qz, qz, P, N0P, R2P);

        var ax = new ulong[4];
        var ay = new ulong[4];
        var az = new ulong[4];
        ScalarMult(ax, ay, az, gx, gy, gz, ToBytes32(u1));

        var bx = new ulong[4];
        var by = new ulong[4];
        var bz = new ulong[4];
        ScalarMult(bx, by, bz, qx, qy, qz, ToBytes32(u2));

        PointAdd(ax, ay, az, bx, by, bz);
        if (IsZero4(az))
            return false;

        var xa = AffineX(ax, az);
        var xp = new ulong[4];
        FromMont(xp, xa, P, N0P);
        ReduceModN(xp);

        return Equal4(xp, r);
    }

    private static bool Equal4(ulong[] a, ulong[] b)
    {
        return a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];
    }

    private static byte[] ToBytes32(ulong[] limbs)
    {
        var b = new byte[32];
        ToBytesBE(b, 0, limbs);
        return b;
    }

    // ==================== DER encoding ====================

    private static byte[] EncodeDer(ulong[] r, ulong[] s)
    {
        byte[] rb = ToBytes32(r);
        byte[] sb = ToBytes32(s);
        byte[] ri = DerInt(rb);
        byte[] si = DerInt(sb);
        int bodyLen = ri.Length + si.Length;
        var der = new byte[2 + bodyLen];
        der[0] = 0x30;
        der[1] = (byte)bodyLen;
        for (int i = 0; i < ri.Length; i++)
            der[2 + i] = ri[i];
        for (int i = 0; i < si.Length; i++)
            der[2 + ri.Length + i] = si[i];
        return der;
    }

    private static byte[] DerInt(byte[] be32)
    {
        int start = 0;
        while (start < 31 && be32[start] == 0)
            start++;
        bool pad = (be32[start] & 0x80) != 0;
        int len = 32 - start + (pad ? 1 : 0);
        var outb = new byte[2 + len];
        outb[0] = 0x02;
        outb[1] = (byte)len;
        int off = 2;
        if (pad)
            outb[off++] = 0;
        for (int i = start; i < 32; i++)
            outb[off++] = be32[i];
        return outb;
    }

    private static bool DecodeDer(byte[] der, ulong[] r, ulong[] s)
    {
        if (der == null || der.Length < 8 || der[0] != 0x30)
            return false;
        int len = der[1];
        if (len > 127 || der.Length < 2 + len)
            return false;
        int pos = 2;
        if (!DecodeDerInt(der, ref pos, r))
            return false;
        if (!DecodeDerInt(der, ref pos, s))
            return false;
        return true;
    }

    private static bool DecodeDerInt(byte[] der, ref int pos, ulong[] outLimbs)
    {
        if (pos + 2 > der.Length || der[pos] != 0x02)
            return false;
        int len = der[pos + 1];
        pos += 2;
        if (pos + len > der.Length || len == 0 || len > 33)
            return false;
        // Skip a leading zero pad; reject anything > 256 bits.
        int start = pos;
        int end = pos + len;
        while (start < end - 1 && der[start] == 0)
            start++;
        if (end - start > 32)
            return false;
        var be = new byte[32];
        int digitCount = end - start;
        for (int i = 0; i < digitCount; i++)
            be[32 - digitCount + i] = der[start + i];
        FromBytesBE(outLimbs, be, 0);
        pos = end;
        return true;
    }
}
