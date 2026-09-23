// ProtonOS DDK - ChaCha20 stream cipher (Phase 6)
//
// Managed C# ChaCha20 (RFC 8439 section 2.4): 256-bit key, 96-bit
// nonce, 32-bit block counter. Used by the TLS 1.3 AEAD
// (TLS_CHACHA20_POLY1305_SHA256, see ChaCha20Poly1305.cs).
//
// Note: OpenSSH's chacha20-poly1305@openssh.com uses a different key
// split / length framing; the SSH server negotiates AES-CTR ciphers
// instead (documented in docs/PHASE6-SSH.md).

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed ChaCha20 stream cipher (see file header).</summary>
public sealed class ChaCha20
{
    private readonly uint[] _key;   // 8 words
    private readonly uint[] _nonce; // 3 words
    private readonly uint _counter;

    /// <summary>Create a ChaCha20 instance from a 32-byte key and 12-byte nonce.</summary>
    public ChaCha20(byte[] key, byte[] nonce, uint counter = 1)
    {
        if (key == null || key.Length != 32)
            throw new ArgumentException("key must be 32 bytes");
        if (nonce == null || nonce.Length != 12)
            throw new ArgumentException("nonce must be 12 bytes");

        _key = new uint[8];
        for (int i = 0; i < 8; i++)
            _key[i] = ReadLE32(key, i * 4);

        _nonce = new uint[3];
        for (int i = 0; i < 3; i++)
            _nonce[i] = ReadLE32(nonce, i * 4);

        _counter = counter;
    }

    /// <summary>First 32 bytes of the keystream for the given key/nonce (Poly1305 one-time key).</summary>
    public static byte[] Poly1305Key(byte[] key, byte[] nonce)
    {
        var c = new ChaCha20(key, nonce, 0);
        byte[] block = new byte[64];
        c.Block(block, 0, 0);
        byte[] otk = new byte[32];
        for (int i = 0; i < 32; i++)
            otk[i] = block[i];
        return otk;
    }

    /// <summary>XOR the keystream into <paramref name="data"/> (encrypt = decrypt).</summary>
    public unsafe void Process(byte* data, int length)
    {
        byte[] block = new byte[64];
        uint counter = _counter;
        int pos = 0;
        while (pos < length)
        {
            Block(block, 0, counter);
            counter++;

            int n = length - pos;
            if (n > 64)
                n = 64;
            for (int i = 0; i < n; i++)
                data[pos + i] ^= block[i];
            pos += n;
        }
    }

    /// <summary>XOR the keystream into a managed buffer.</summary>
    public void Process(byte[] data, int offset, int length)
    {
        byte[] block = new byte[64];
        uint counter = _counter;
        int pos = 0;
        while (pos < length)
        {
            Block(block, 0, counter);
            counter++;

            int n = length - pos;
            if (n > 64)
                n = 64;
            for (int i = 0; i < n; i++)
                data[offset + pos + i] ^= block[i];
            pos += n;
        }
    }

    private void Block(byte[] outBlock, int offset, uint counter)
    {
        uint[] state = new uint[16];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;
        for (int i = 0; i < 8; i++)
            state[4 + i] = _key[i];
        state[12] = counter;
        state[13] = _nonce[0];
        state[14] = _nonce[1];
        state[15] = _nonce[2];

        uint[] working = new uint[16];
        for (int i = 0; i < 16; i++)
            working[i] = state[i];

        for (int round = 0; round < 10; round++)
        {
            // Column rounds.
            Qr(working, 0, 4, 8, 12);
            Qr(working, 1, 5, 9, 13);
            Qr(working, 2, 6, 10, 14);
            Qr(working, 3, 7, 11, 15);
            // Diagonal rounds.
            Qr(working, 0, 5, 10, 15);
            Qr(working, 1, 6, 11, 12);
            Qr(working, 2, 7, 8, 13);
            Qr(working, 3, 4, 9, 14);
        }

        for (int i = 0; i < 16; i++)
            WriteLE32(outBlock, offset + i * 4, working[i] + state[i]);
    }

    private static void Qr(uint[] s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] ^= s[a]; s[d] = Rotl(s[d], 16);
        s[c] += s[d]; s[b] ^= s[c]; s[b] = Rotl(s[b], 12);
        s[a] += s[b]; s[d] ^= s[a]; s[d] = Rotl(s[d], 8);
        s[c] += s[d]; s[b] ^= s[c]; s[b] = Rotl(s[b], 7);
    }

    private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));

    private static uint ReadLE32(byte[] b, int i)
        => (uint)b[i] | ((uint)b[i + 1] << 8) | ((uint)b[i + 2] << 16) | ((uint)b[i + 3] << 24);

    private static void WriteLE32(byte[] b, int i, uint v)
    {
        b[i] = (byte)v;
        b[i + 1] = (byte)(v >> 8);
        b[i + 2] = (byte)(v >> 16);
        b[i + 3] = (byte)(v >> 24);
    }
}
