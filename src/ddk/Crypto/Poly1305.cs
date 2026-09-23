// ProtonOS DDK - Poly1305 one-time authenticator (Phase 6)
//
// Managed C# Poly1305 (RFC 8439 section 2.5) using 26-bit limbs and
// 64-bit intermediate arithmetic (the classic "poly1305-donna" layout).
// Used by the TLS 1.3 ChaCha20-Poly1305 AEAD.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed Poly1305 MAC (see file header).</summary>
public sealed class Poly1305
{
    // Accumulator and key material as 5 x 26-bit limbs.
    private readonly uint[] _r = new uint[5];
    private readonly uint[] _h = new uint[5];
    private readonly uint[] _pad = new uint[4];

    /// <summary>Create a Poly1305 instance with a 32-byte one-time key.</summary>
    public Poly1305(byte[] key)
    {
        if (key == null || key.Length != 32)
            throw new ArgumentException("one-time key must be 32 bytes");

        uint t0 = Le(key, 0);
        uint t1 = Le(key, 4);
        uint t2 = Le(key, 8);
        uint t3 = Le(key, 12);

        // r clamped: r &= 0x0ffffffc0ffffffc0ffffffc0fffffff
        _r[0] = t0 & 0x3ffffff;
        _r[1] = ((t0 >> 26) | (t1 << 6)) & 0x3ffff03;
        _r[2] = ((t1 >> 20) | (t2 << 12)) & 0x3ffc0ff;
        _r[3] = ((t2 >> 14) | (t3 << 18)) & 0x3f03fff;
        _r[4] = (t3 >> 8) & 0x00fffff;

        _pad[0] = Le(key, 16);
        _pad[1] = Le(key, 20);
        _pad[2] = Le(key, 24);
        _pad[3] = Le(key, 28);
    }

    /// <summary>Feed a full 16-byte block adding the 2^128 bit (RFC 8439).</summary>
    public void Block(byte[] data, int offset, int length, uint hibit)
    {
        uint t0 = Le(data, offset);
        uint t1 = Le(data, offset + 4);
        uint t2 = Le(data, offset + 8);
        uint t3 = Le(data, offset + 12);

        // h += m
        _h[0] += t0 & 0x3ffffff;
        _h[1] += ((t0 >> 26) | (t1 << 6)) & 0x3ffffff;
        _h[2] += ((t1 >> 20) | (t2 << 12)) & 0x3ffffff;
        _h[3] += ((t2 >> 14) | (t3 << 18)) & 0x3ffffff;
        _h[4] += (t3 >> 8) | hibit;

        // h *= r  (schoolbook over 26-bit limbs, m = 2^130 - 5;
        // the five-times terms wrap limb positions by 2^130 = 5).
        ulong d0 = (ulong)_h[0] * _r[0] + (ulong)_h[1] * R4() + (ulong)_h[2] * R3() + (ulong)_h[3] * R2() + (ulong)_h[4] * R1();
        ulong d1 = (ulong)_h[0] * _r[1] + (ulong)_h[1] * _r[0] + (ulong)_h[2] * R4() + (ulong)_h[3] * R3() + (ulong)_h[4] * R2();
        ulong d2 = (ulong)_h[0] * _r[2] + (ulong)_h[1] * _r[1] + (ulong)_h[2] * _r[0] + (ulong)_h[3] * R4() + (ulong)_h[4] * R3();
        ulong d3 = (ulong)_h[0] * _r[3] + (ulong)_h[1] * _r[2] + (ulong)_h[2] * _r[1] + (ulong)_h[3] * _r[0] + (ulong)_h[4] * R4();
        ulong d4 = (ulong)_h[0] * _r[4] + (ulong)_h[1] * _r[3] + (ulong)_h[2] * _r[2] + (ulong)_h[3] * _r[1] + (ulong)_h[4] * _r[0];

        // Carry propagation.
        ulong c = d0 >> 26; _h[0] = (uint)d0 & 0x3ffffff;
        d1 += c; c = d1 >> 26; _h[1] = (uint)d1 & 0x3ffffff;
        d2 += c; c = d2 >> 26; _h[2] = (uint)d2 & 0x3ffffff;
        d3 += c; c = d3 >> 26; _h[3] = (uint)d3 & 0x3ffffff;
        d4 += c; c = d4 >> 26; _h[4] = (uint)d4 & 0x3ffffff;
        _h[0] += (uint)(c * 5); c = _h[0] >> 26; _h[0] &= 0x3ffffff;
        _h[1] += (uint)c;
    }

    private uint R1() => _r[1] * 5;
    private uint R2() => _r[2] * 5;
    private uint R3() => _r[3] * 5;
    private uint R4() => _r[4] * 5;

    /// <summary>Finish and produce the 16-byte tag.</summary>
    public byte[] Finish()
    {
        // Fully carry h.
        ulong c = _h[1] >> 26; _h[1] &= 0x3ffffff;
        _h[2] += (uint)c; c = _h[2] >> 26; _h[2] &= 0x3ffffff;
        _h[3] += (uint)c; c = _h[3] >> 26; _h[3] &= 0x3ffffff;
        _h[4] += (uint)c; c = _h[4] >> 26; _h[4] &= 0x3ffffff;
        _h[0] += (uint)(c * 5); c = _h[0] >> 26; _h[0] &= 0x3ffffff;
        _h[1] += (uint)c;

        // Compute h + -p (i.e. h + 5 - 2^130) to test for the >= p case.
        uint g0 = _h[0] + 5; c = g0 >> 26; g0 &= 0x3ffffff;
        uint g1 = _h[1] + (uint)c; c = g1 >> 26; g1 &= 0x3ffffff;
        uint g2 = _h[2] + (uint)c; c = g2 >> 26; g2 &= 0x3ffffff;
        uint g3 = _h[3] + (uint)c; c = g3 >> 26; g3 &= 0x3ffffff;
        uint g4 = _h[4] + (uint)c - (1u << 26);

        // Select h if g4 has the high bit set (no underflow => h < p).
        uint mask = (g4 >> 31) - 1;
        g0 &= mask; g1 &= mask; g2 &= mask; g3 &= mask; g4 &= mask;
        uint nmask = ~mask;
        _h[0] = (_h[0] & nmask) | g0;
        _h[1] = (_h[1] & nmask) | g1;
        _h[2] = (_h[2] & nmask) | g2;
        _h[3] = (_h[3] & nmask) | g3;
        _h[4] = (_h[4] & nmask) | g4;

        // h = h + pad (mod 2^128), with each 32-bit word masked
        // before the carry-add (the OR can set bits above bit 31).
        ulong f0 = (((ulong)_h[0] | ((ulong)_h[1] << 26)) & 0xFFFFFFFF) + _pad[0];
        ulong f1 = ((((ulong)(_h[1] >> 6)) | ((ulong)_h[2] << 20)) & 0xFFFFFFFF) + _pad[1] + (f0 >> 32);
        ulong f2 = ((((ulong)(_h[2] >> 12)) | ((ulong)_h[3] << 14)) & 0xFFFFFFFF) + _pad[2] + (f1 >> 32);
        ulong f3 = ((((ulong)(_h[3] >> 18)) | ((ulong)_h[4] << 8)) & 0xFFFFFFFF) + _pad[3] + (f2 >> 32);

        byte[] tag = new byte[16];
        WriteLe(tag, 0, (uint)f0);
        WriteLe(tag, 4, (uint)f1);
        WriteLe(tag, 8, (uint)f2);
        WriteLe(tag, 12, (uint)f3);
        return tag;
    }

    /// <summary>One-shot Poly1305 over the given message.</summary>
    public static byte[] Compute(byte[] key, byte[] message, int offset, int length)
    {
        var p = new Poly1305(key);
        int pos = 0;
        while (pos < length)
        {
            int n = length - pos;
            if (n > 16)
                n = 16;
            if (n == 16)
            {
                p.Block(message, offset + pos, 16, 1u << 24);
            }
            else
            {
                // Final partial block: append 0x01 then zero-fill.
                byte[] last = new byte[16];
                for (int i = 0; i < n; i++)
                    last[i] = message[offset + pos + i];
                last[n] = 0x01;
                p.Block(last, 0, 16, 0);
            }
            pos += n;
        }
        if (length == 0)
        {
            // Empty message: single block of 0x01 (RFC 8439).
            byte[] last = new byte[16];
            last[0] = 0x01;
            p.Block(last, 0, 16, 0);
        }
        return p.Finish();
    }

    private static uint Le(byte[] b, int i)
        => (uint)b[i] | ((uint)b[i + 1] << 8) | ((uint)b[i + 2] << 16) | ((uint)b[i + 3] << 24);

    private static void WriteLe(byte[] b, int i, uint v)
    {
        b[i] = (byte)v;
        b[i + 1] = (byte)(v >> 8);
        b[i + 2] = (byte)(v >> 16);
        b[i + 3] = (byte)(v >> 24);
    }
}
