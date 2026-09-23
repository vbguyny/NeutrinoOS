// ProtonOS DDK - AES-GCM AEAD (Phase 6)
//
// Managed C# AES-GCM (NIST SP 800-38D) built on the AES core and a
// bitwise GHASH over GF(2^128). Used by TLS 1.3 (AES-128/256-GCM) and
// available to SSH as aes*-gcm@openssh.com if negotiated.
//
// GHASH here is the straightforward bit-by-bit reduction (128 rounds
// per block); throughput is adequate for the Phase 6 console workloads
// and keeps the implementation easy to audit.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed AES-GCM authenticated encryption (see file header).</summary>
public sealed class AesGcm
{
    private readonly Aes _aes;
    private readonly byte[] _h = new byte[16];  // GHASH key = E(0^128)

    /// <summary>Tag size in bytes produced by <see cref="Seal"/>.</summary>
    public const int TagSize = 16;

    /// <summary>Create an AES-GCM instance with a 16/24/32-byte key.</summary>
    public AesGcm(byte[] key)
    {
        _aes = new Aes(key);
        byte[] zero = new byte[16];
        _aes.EncryptBlock(zero, 0);
        for (int i = 0; i < 16; i++)
            _h[i] = zero[i];
    }

    /// <summary>Seal: encrypt plaintext and produce ciphertext + 16-byte tag.</summary>
    public byte[] Seal(byte[] nonce, byte[] aad, byte[] plaintext)
    {
        byte[] j0 = ComputeJ0(nonce, out bool derived);
        _ = derived;

        // Ciphertext = GCTR(J0 + 1, plaintext)
        byte[] ctr = new byte[16];
        for (int i = 0; i < 16; i++)
            ctr[i] = j0[i];
        Inc32(ctr);

        byte[] ciphertext = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++)
            ciphertext[i] = plaintext[i];
        RunCtr(ciphertext, 0, ciphertext.Length, ctr);

        byte[] tag = ComputeTag(j0, aad, ciphertext);
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

        byte[] j0 = ComputeJ0(nonce, out bool derived);
        _ = derived;

        byte[] expect = ComputeTag(j0, aad, Slice(ciphertext, offset, length));
        int diff = 0;
        for (int i = 0; i < 16; i++)
            diff |= expect[i] ^ tag[i];
        if (diff != 0)
            return false;

        byte[] ctr = new byte[16];
        for (int i = 0; i < 16; i++)
            ctr[i] = j0[i];
        Inc32(ctr);

        byte[] pt = Slice(ciphertext, offset, length);
        RunCtr(pt, 0, pt.Length, ctr);
        plaintext = pt;
        return true;
    }

    private byte[] ComputeJ0(byte[] nonce, out bool derived)
    {
        derived = false;
        byte[] j0 = new byte[16];
        if (nonce.Length == 12)
        {
            for (int i = 0; i < 12; i++)
                j0[i] = nonce[i];
            j0[15] = 1;
            return j0;
        }

        // General case: J0 = GHASH(nonce padded, len(nonce) bits).
        derived = true;
        byte[] s = new byte[16];
        int pos = 0;
        while (pos < nonce.Length)
        {
            byte[] block = new byte[16];
            int n = nonce.Length - pos;
            if (n > 16)
                n = 16;
            for (int i = 0; i < n; i++)
                block[i] = nonce[pos + i];
            XorBlock(s, block);
            GHashMul(s);
            pos += n;
        }
        byte[] lenBlock = new byte[16];
        ulong bits = (ulong)nonce.Length * 8;
        for (int i = 0; i < 8; i++)
            lenBlock[8 + i] = (byte)(bits >> (56 - i * 8));
        XorBlock(s, lenBlock);
        GHashMul(s);
        return s;
    }

    private byte[] ComputeTag(byte[] j0, byte[] aad, byte[] ciphertext)
    {
        byte[] s = new byte[16];

        // GHASH over AAD (zero padded).
        if (aad != null)
        {
            int pos = 0;
            while (pos < aad.Length)
            {
                byte[] block = new byte[16];
                int n = aad.Length - pos;
                if (n > 16)
                    n = 16;
                for (int i = 0; i < n; i++)
                    block[i] = aad[pos + i];
                XorBlock(s, block);
                GHashMul(s);
                pos += n;
            }
        }

        // GHASH over ciphertext (zero padded).
        {
            int pos = 0;
            while (pos < ciphertext.Length)
            {
                byte[] block = new byte[16];
                int n = ciphertext.Length - pos;
                if (n > 16)
                    n = 16;
                for (int i = 0; i < n; i++)
                    block[i] = ciphertext[pos + i];
                XorBlock(s, block);
                GHashMul(s);
                pos += n;
            }
        }

        // Length block: [len(A)]64 || [len(C)]64 in bits.
        byte[] lenBlock = new byte[16];
        ulong aadBits = aad == null ? 0 : (ulong)aad.Length * 8;
        ulong ctBits = (ulong)ciphertext.Length * 8;
        for (int i = 0; i < 8; i++)
        {
            lenBlock[i] = (byte)(aadBits >> (56 - i * 8));
            lenBlock[8 + i] = (byte)(ctBits >> (56 - i * 8));
        }
        XorBlock(s, lenBlock);
        GHashMul(s);

        // Tag = E(J0) ^ S
        byte[] tag = new byte[16];
        for (int i = 0; i < 16; i++)
            tag[i] = j0[i];
        _aes.EncryptBlock(tag, 0);
        for (int i = 0; i < 16; i++)
            tag[i] ^= s[i];
        return tag;
    }

    private void RunCtr(byte[] data, int offset, int length, byte[] counter)
    {
        byte[] ks = new byte[16];
        int pos = 0;
        while (pos < length)
        {
            for (int i = 0; i < 16; i++)
                ks[i] = counter[i];
            _aes.EncryptBlock(ks, 0);

            int n = length - pos;
            if (n > 16)
                n = 16;
            for (int i = 0; i < n; i++)
                data[offset + pos + i] ^= ks[i];

            Inc32(counter);
            pos += n;
        }
    }

    private static void Inc32(byte[] counter)
    {
        for (int i = 15; i >= 12; i--)
        {
            counter[i]++;
            if (counter[i] != 0)
                break;
        }
    }

    private static void XorBlock(byte[] s, byte[] block)
    {
        for (int i = 0; i < 16; i++)
            s[i] ^= block[i];
    }

    /// <summary>Multiply the GHASH state by H in GF(2^128) (bitwise).</summary>
    private void GHashMul(byte[] s)
    {
        byte[] z = new byte[16];
        byte[] v = new byte[16];
        for (int i = 0; i < 16; i++)
            v[i] = _h[i];

        for (int bit = 0; bit < 128; bit++)
        {
            // If s bit (bit) is set, z ^= v.
            int byteIdx = bit / 8;
            int bitIdx = 7 - (bit % 8);
            if (((s[byteIdx] >> bitIdx) & 1) != 0)
            {
                for (int i = 0; i < 16; i++)
                    z[i] ^= v[i];
            }

            // v = v >> 1 with reduction polynomial R = 0xE1.
            bool lsb = (v[15] & 1) != 0;
            for (int i = 15; i > 0; i--)
                v[i] = (byte)((v[i] >> 1) | ((v[i - 1] & 1) << 7));
            v[0] = (byte)(v[0] >> 1);
            if (lsb)
                v[0] ^= 0xE1;
        }

        for (int i = 0; i < 16; i++)
            s[i] = z[i];
    }

    private static byte[] Slice(byte[] data, int offset, int length)
    {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = data[offset + i];
        return result;
    }
}
