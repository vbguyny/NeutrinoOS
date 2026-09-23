// ProtonOS DDK - AES core + CTR mode (Phase 6)
//
// Managed C# AES (FIPS 197) block cipher with key sizes 128/192/256
// and CTR mode (NIST SP 800-38A). Only the ENCRYPT direction is
// implemented: SSH (aes*-ctr) and TLS 1.3 (AES-GCM) never need the
// inverse cipher. The S-box is generated at class-init from GF(2^8)
// arithmetic so no lookup tables are transcribed by hand.
//
// Runs under the Tier-0 JIT: arrays + 32-bit integer ops only.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed AES block cipher + CTR mode (see file header).</summary>
public sealed class Aes
{
    private readonly int _rounds;
    private readonly uint[] _roundKeys;   // (rounds + 1) * 4 words

    /// <summary>Create an AES instance with a 16, 24 or 32-byte key.</summary>
    public Aes(byte[] key)
    {
        if (key == null)
            throw new ArgumentException("key required");
        int keyLen = key.Length;
        if (keyLen != 16 && keyLen != 24 && keyLen != 32)
            throw new ArgumentException("key must be 16, 24 or 32 bytes");

        int nk = keyLen / 4;
        _rounds = nk + 6;
        _roundKeys = new uint[(_rounds + 1) * 4];
        ExpandKey(key, nk);
    }

    /// <summary>Block size in bytes (16).</summary>
    public int BlockSize => 16;

    /// <summary>Number of rounds for the current key size.</summary>
    public int Rounds => _rounds;

    /// <summary>Encrypt one 16-byte block (in place).</summary>
    public void EncryptBlock(byte[] block, int offset)
    {
        uint s0 = ReadBE32(block, offset);
        uint s1 = ReadBE32(block, offset + 4);
        uint s2 = ReadBE32(block, offset + 8);
        uint s3 = ReadBE32(block, offset + 12);

        AddRoundKey(0, ref s0, ref s1, ref s2, ref s3);

        for (int round = 1; round < _rounds; round++)
        {
            SubBytesAndShift(ref s0, ref s1, ref s2, ref s3);
            MixColumns(ref s0, ref s1, ref s2, ref s3);
            AddRoundKey(round, ref s0, ref s1, ref s2, ref s3);
        }

        SubBytesAndShift(ref s0, ref s1, ref s2, ref s3);
        AddRoundKey(_rounds, ref s0, ref s1, ref s2, ref s3);

        WriteBE32(block, offset, s0);
        WriteBE32(block, offset + 4, s1);
        WriteBE32(block, offset + 8, s2);
        WriteBE32(block, offset + 12, s3);
    }

    /// <summary>Encrypt one 16-byte block (unmanaged in/out).</summary>
    public unsafe void EncryptBlock(byte* block)
    {
        uint s0 = ReadBE32(block, 0);
        uint s1 = ReadBE32(block, 4);
        uint s2 = ReadBE32(block, 8);
        uint s3 = ReadBE32(block, 12);

        AddRoundKey(0, ref s0, ref s1, ref s2, ref s3);
        for (int round = 1; round < _rounds; round++)
        {
            SubBytesAndShift(ref s0, ref s1, ref s2, ref s3);
            MixColumns(ref s0, ref s1, ref s2, ref s3);
            AddRoundKey(round, ref s0, ref s1, ref s2, ref s3);
        }
        SubBytesAndShift(ref s0, ref s1, ref s2, ref s3);
        AddRoundKey(_rounds, ref s0, ref s1, ref s2, ref s3);

        WriteBE32(block, 0, s0);
        WriteBE32(block, 4, s1);
        WriteBE32(block, 8, s2);
        WriteBE32(block, 12, s3);
    }

    // ==================== CTR mode ====================

    /// <summary>
    /// Process (encrypt or decrypt - identical for CTR) a buffer in
    /// place. The counter block is incremented as a 128-bit big-endian
    /// integer after each block. The Phase 6 usage (SSH aes*-ctr, TLS
    /// GCM) always operates from a fresh key/IV per direction.
    /// </summary>
    public unsafe void CtrXor(byte* data, int length, byte* counter)
    {
        byte* keystream = stackalloc byte[16];
        int pos = 0;
        while (pos < length)
        {
            for (int i = 0; i < 16; i++)
                keystream[i] = counter[i];
            EncryptBlock(keystream);

            int n = length - pos;
            if (n > 16)
                n = 16;
            for (int i = 0; i < n; i++)
                data[pos + i] ^= keystream[i];

            // 128-bit big-endian increment.
            for (int i = 15; i >= 0; i--)
            {
                counter[i]++;
                if (counter[i] != 0)
                    break;
            }
            pos += n;
        }
    }

    // ==================== Key schedule ====================

    private static readonly byte[] Rcon = new byte[11]
    {
        0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36,
    };

    private void ExpandKey(byte[] key, int nk)
    {
        int totalWords = (_rounds + 1) * 4;
        for (int i = 0; i < nk; i++)
            _roundKeys[i] = ((uint)key[i * 4] << 24) | ((uint)key[i * 4 + 1] << 16) |
                            ((uint)key[i * 4 + 2] << 8) | key[i * 4 + 3];

        for (int i = nk; i < totalWords; i++)
        {
            uint temp = _roundKeys[i - 1];
            if (i % nk == 0)
            {
                // RotWord + SubWord + Rcon.
                temp = (temp << 8) | (temp >> 24);
                temp = SubWord(temp);
                temp ^= (uint)Rcon[i / nk] << 24;
            }
            else if (nk > 6 && i % nk == 4)
            {
                temp = SubWord(temp);
            }
            _roundKeys[i] = _roundKeys[i - nk] ^ temp;
        }
    }

    private static uint SubWord(uint w)
    {
        return ((uint)SBox[(w >> 24) & 0xFF] << 24) |
               ((uint)SBox[(w >> 16) & 0xFF] << 16) |
               ((uint)SBox[(w >> 8) & 0xFF] << 8) |
               SBox[w & 0xFF];
    }

    private void AddRoundKey(int round, ref uint s0, ref uint s1, ref uint s2, ref uint s3)
    {
        int o = round * 4;
        s0 ^= _roundKeys[o];
        s1 ^= _roundKeys[o + 1];
        s2 ^= _roundKeys[o + 2];
        s3 ^= _roundKeys[o + 3];
    }

    private static void SubBytesAndShift(ref uint s0, ref uint s1, ref uint s2, ref uint s3)
    {
        // SubBytes on the flattened state (column-major words), then
        // ShiftRows using the row-rotation form:
        //  row 0: unchanged; row 1 <<< 1; row 2 <<< 2; row 3 <<< 3.
        uint t0 = SubWord(s0);
        uint t1 = SubWord(s1);
        uint t2 = SubWord(s2);
        uint t3 = SubWord(s3);

        s0 = (t0 & 0xFF000000) | (t1 & 0x00FF0000) | (t2 & 0x0000FF00) | (t3 & 0x000000FF);
        s1 = (t1 & 0xFF000000) | (t2 & 0x00FF0000) | (t3 & 0x0000FF00) | (t0 & 0x000000FF);
        s2 = (t2 & 0xFF000000) | (t3 & 0x00FF0000) | (t0 & 0x0000FF00) | (t1 & 0x000000FF);
        s3 = (t3 & 0xFF000000) | (t0 & 0x00FF0000) | (t1 & 0x0000FF00) | (t2 & 0x000000FF);
    }

    private static uint Gmul2(uint x) => ((x << 1) ^ ((x & 0x80) != 0 ? 0x1Bu : 0u)) & 0xFF;

    private static uint Gmul3(uint x) => Gmul2(x) ^ x;

    private static void MixColumns(ref uint s0, ref uint s1, ref uint s2, ref uint s3)
    {
        s0 = MixWord(s0);
        s1 = MixWord(s1);
        s2 = MixWord(s2);
        s3 = MixWord(s3);
    }

    private static uint MixWord(uint w)
    {
        uint c0 = (w >> 24) & 0xFF, c1 = (w >> 16) & 0xFF, c2 = (w >> 8) & 0xFF, c3 = w & 0xFF;
        uint r0 = Gmul2(c0) ^ Gmul3(c1) ^ c2 ^ c3;
        uint r1 = c0 ^ Gmul2(c1) ^ Gmul3(c2) ^ c3;
        uint r2 = c0 ^ c1 ^ Gmul2(c2) ^ Gmul3(c3);
        uint r3 = Gmul3(c0) ^ c1 ^ c2 ^ Gmul2(c3);
        return (r0 << 24) | (r1 << 16) | (r2 << 8) | r3;
    }

    // ==================== S-box generation ====================

    /// <summary>AES S-box, generated from GF(2^8) inverse + affine map.</summary>
    internal static readonly byte[] SBox = BuildSBox();

    private static byte[] BuildSBox()
    {
        // exp/log tables over GF(2^8) with the AES polynomial 0x11B,
        // generator 3 (x ^= (x << 1) ^ (bit7 ? 0x1B : 0)).
        byte[] exp = new byte[512];
        byte[] log = new byte[256];
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            exp[i] = (byte)x;
            log[x] = (byte)i;
            // Multiply by 3 (the generator x + 1): 3*x = (2*x) ^ x.
            // (2 alone only spans a 51-element subgroup of this field.)
            int doubled = (x << 1) ^ ((x & 0x80) != 0 ? 0x1B : 0);
            x = (doubled ^ x) & 0xFF;
        }
        for (int i = 255; i < 512; i++)
            exp[i] = exp[i - 255];

        byte[] sbox = new byte[256];
        for (int a = 0; a < 256; a++)
        {
            byte inv = a == 0 ? (byte)0 : exp[255 - log[a]];
            byte b = inv;
            byte s = (byte)(b ^ Rotl8(b, 1) ^ Rotl8(b, 2) ^ Rotl8(b, 3) ^ Rotl8(b, 4) ^ 0x63);
            sbox[a] = s;
        }
        return sbox;
    }

    private static byte Rotl8(byte b, int n) => (byte)(((b << n) | (b >> (8 - n))) & 0xFF);

    // ==================== Byte helpers ====================

    private static uint ReadBE32(byte[] b, int i)
        => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

    private static unsafe uint ReadBE32(byte* b, int i)
        => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

    private static void WriteBE32(byte[] b, int i, uint v)
    {
        b[i] = (byte)(v >> 24);
        b[i + 1] = (byte)(v >> 16);
        b[i + 2] = (byte)(v >> 8);
        b[i + 3] = (byte)v;
    }

    private static unsafe void WriteBE32(byte* b, int i, uint v)
    {
        b[i] = (byte)(v >> 24);
        b[i + 1] = (byte)(v >> 16);
        b[i + 2] = (byte)(v >> 8);
        b[i + 3] = (byte)v;
    }
}
