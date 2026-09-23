// ProtonOS DDK - HKDF (Phase 6)
//
// Managed C# HKDF (RFC 5869) built on the Phase 6 HMAC. Used by the
// TLS 1.3 key schedule and SSH key derivation (RFC 4253 section 7.2
// for the older scheme; curve25519-sha256 uses the same HMAC loop).

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>HKDF-Extract/Expand over the Phase 6 hash primitives.</summary>
public sealed class Hkdf
{
    /// <summary>
    /// HKDF-Extract: PRK = HMAC(salt, IKM). An empty salt is substituted
    /// with a zero-filled block per RFC 5869.
    /// </summary>
    public static byte[] Extract(HashKind kind, byte[] salt, byte[] ikm, int ikmOffset, int ikmLength)
    {
        int block = Hmac.BlockSizeFor(kind);
        if (salt == null || salt.Length == 0)
            salt = new byte[block];

        var h = new Hmac(kind, salt);
        h.Update(ikm, ikmOffset, ikmLength);
        return h.Finish();
    }

    /// <summary>HKDF-Extract over an unmanaged IKM buffer.</summary>
    public static unsafe byte[] Extract(HashKind kind, byte[] salt, byte* ikm, int ikmLength)
    {
        int block = Hmac.BlockSizeFor(kind);
        if (salt == null || salt.Length == 0)
            salt = new byte[block];

        var h = new Hmac(kind, salt);
        h.Update(ikm, ikmLength);
        return h.Finish();
    }

    /// <summary>
    /// HKDF-Expand: T(n) = HMAC(PRK, T(n-1) | info | n), output the
    /// first <paramref name="length"/> bytes.
    /// </summary>
    public static byte[] Expand(HashKind kind, byte[] prk, byte[] info, int length)
    {
        int ds = Hmac.DigestSizeFor(kind);
        byte[] output = new byte[length];
        byte[] t = new byte[0];
        int pos = 0;
        byte counter = 1;

        while (pos < length)
        {
            var h = new Hmac(kind, prk);
            h.Update(t, 0, t.Length);
            if (info != null && info.Length > 0)
                h.Update(info, 0, info.Length);
            byte[] c = new byte[1];
            c[0] = counter;
            h.Update(c, 0, 1);
            t = h.Finish();

            int copy = length - pos;
            if (copy > ds)
                copy = ds;
            for (int i = 0; i < copy; i++)
                output[pos + i] = t[i];
            pos += copy;
            counter++;
        }
        return output;
    }

    /// <summary>One-shot HKDF (Extract + Expand).</summary>
    public static byte[] Derive(HashKind kind, byte[] salt, byte[] ikm, byte[] info, int length)
    {
        byte[] prk = Extract(kind, salt, ikm, 0, ikm.Length);
        return Expand(kind, prk, info, length);
    }

    /// <summary>
    /// Expand matching <see cref="Derive"/>; writes into an unmanaged
    /// buffer and returns the number of bytes written.
    /// </summary>
    public static unsafe int ExpandTo(HashKind kind, byte[] prk, byte[] info, byte* output, int length)
    {
        byte[] tmp = Expand(kind, prk, info, length);
        for (int i = 0; i < tmp.Length; i++)
            output[i] = tmp[i];
        return tmp.Length;
    }
}
