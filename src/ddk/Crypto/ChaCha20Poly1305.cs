// ProtonOS DDK - ChaCha20-Poly1305 AEAD (Phase 6)
//
// Managed C# ChaCha20-Poly1305 (RFC 8439 section 2.8) built on the
// Phase 6 ChaCha20 and Poly1305 primitives. Used by TLS 1.3
// (TLS_CHACHA20_POLY1305_SHA256).

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed ChaCha20-Poly1305 authenticated encryption (RFC 8439).</summary>
public sealed class ChaCha20Poly1305
{
    /// <summary>Tag size in bytes produced by <see cref="Seal"/>.</summary>
    public const int TagSize = 16;

    private readonly byte[] _key;

    /// <summary>Create an instance with a 32-byte key.</summary>
    public ChaCha20Poly1305(byte[] key)
    {
        if (key == null || key.Length != 32)
            throw new ArgumentException("key must be 32 bytes");
        _key = key;
    }

    /// <summary>Seal: encrypt plaintext, returning ciphertext + 16-byte tag.</summary>
    public byte[] Seal(byte[] nonce, byte[] aad, byte[] plaintext)
    {
        byte[] otk = ChaCha20.Poly1305Key(_key, nonce);

        byte[] ciphertext = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++)
            ciphertext[i] = plaintext[i];
        var c = new ChaCha20(_key, nonce, 1);
        c.Process(ciphertext, 0, ciphertext.Length);

        byte[] macData = BuildMacData(aad, ciphertext);
        byte[] tag = Poly1305.Compute(otk, macData, 0, macData.Length);

        byte[] output = new byte[ciphertext.Length + 16];
        for (int i = 0; i < ciphertext.Length; i++)
            output[i] = ciphertext[i];
        for (int i = 0; i < 16; i++)
            output[ciphertext.Length + i] = tag[i];
        return output;
    }

    /// <summary>
    /// Open: verify the tag and decrypt. Returns false (and null
    /// plaintext) when the tag does not match.
    /// </summary>
    public bool Open(byte[] nonce, byte[] aad, byte[] ciphertext, int offset, int length,
        byte[] tag, out byte[] plaintext)
    {
        plaintext = null;

        byte[] otk = ChaCha20.Poly1305Key(_key, nonce);
        byte[] ct = new byte[length];
        for (int i = 0; i < length; i++)
            ct[i] = ciphertext[offset + i];

        byte[] macData = BuildMacData(aad, ct);
        byte[] expect = Poly1305.Compute(otk, macData, 0, macData.Length);

        int diff = 0;
        for (int i = 0; i < 16; i++)
            diff |= expect[i] ^ tag[i];
        if (diff != 0)
            return false;

        var c = new ChaCha20(_key, nonce, 1);
        c.Process(ct, 0, ct.Length);
        plaintext = ct;
        return true;
    }

    private static byte[] BuildMacData(byte[] aad, byte[] ciphertext)
    {
        int aadLen = aad == null ? 0 : aad.Length;
        int ctLen = ciphertext.Length;
        int total = Align16(aadLen) + Align16(ctLen) + 16;

        byte[] mac = new byte[total];
        int pos = 0;
        if (aadLen > 0)
        {
            for (int i = 0; i < aadLen; i++)
                mac[pos + i] = aad[i];
            pos += Align16(aadLen);
        }
        for (int i = 0; i < ctLen; i++)
            mac[pos + i] = ciphertext[i];
        pos += Align16(ctLen);

        // RFC 8439: the trailer carries the AAD and ciphertext lengths in
        // BYTES (little-endian 64-bit each). (GCM uses bits; Poly1305 does not.)
        ulong aadLen64 = (ulong)aadLen;
        ulong ctLen64 = (ulong)ctLen;
        for (int i = 0; i < 8; i++)
        {
            mac[pos + i] = (byte)(aadLen64 >> (i * 8));
            mac[pos + 8 + i] = (byte)(ctLen64 >> (i * 8));
        }
        return mac;
    }

    private static int Align16(int n) => (n + 15) & ~15;
}
