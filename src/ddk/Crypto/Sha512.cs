// ProtonOS DDK - SHA-512 and SHA-384 (Phase 6)
//
// Managed C# SHA-512/SHA-384 (FIPS 180-4). Used by HMAC-SHA512,
// TLS 1.3 key schedule and SSH (rsa-sha2-512, hmac-sha2-512).

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>
/// Shared SHA-512 compression engine; SHA-384 is SHA-512 with a different
/// IV and a truncated (48-byte) output.
/// </summary>
internal sealed class Sha512Engine
{
    private const int BlockSize = 128;

    private readonly ulong[] _state;
    private readonly byte[] _buffer;
    private readonly bool _is384;
    private int _bufferLen;
    private ulong _totalBytes;

    internal Sha512Engine(bool sha384)
    {
        _state = new ulong[8];
        _buffer = new byte[BlockSize];
        _is384 = sha384;
        Reset();
    }

    internal int DigestSize => _is384 ? 48 : 64;

    internal void Reset()
    {
        if (_is384)
        {
            _state[0] = 0xcbbb9d5dc1059ed8;
            _state[1] = 0x629a292a367cd507;
            _state[2] = 0x9159015a3070dd17;
            _state[3] = 0x152fecd8f70e5939;
            _state[4] = 0x67332667ffc00b31;
            _state[5] = 0x8eb44a8768581511;
            _state[6] = 0xdb0c2e0d64f98fa7;
            _state[7] = 0x47b5481dbefa4fa4;
        }
        else
        {
            _state[0] = 0x6a09e667f3bcc908;
            _state[1] = 0xbb67ae8584caa73b;
            _state[2] = 0x3c6ef372fe94f82b;
            _state[3] = 0xa54ff53a5f1d36f1;
            _state[4] = 0x510e527fade682d1;
            _state[5] = 0x9b05688c2b3e6c1f;
            _state[6] = 0x1f83d9abfb41bd6b;
            _state[7] = 0x5be0cd19137e2179;
        }
        _bufferLen = 0;
        _totalBytes = 0;
    }

    internal void Update(byte[] data, int offset, int count)
    {
        _totalBytes += (ulong)count;
        for (int i = 0; i < count; i++)
        {
            _buffer[_bufferLen++] = data[offset + i];
            if (_bufferLen == BlockSize)
            {
                ProcessBlock(_buffer, 0);
                _bufferLen = 0;
            }
        }
    }

    internal unsafe void Update(byte* data, int count)
    {
        _totalBytes += (ulong)count;
        for (int i = 0; i < count; i++)
        {
            _buffer[_bufferLen++] = data[i];
            if (_bufferLen == BlockSize)
            {
                ProcessBlock(_buffer, 0);
                _bufferLen = 0;
            }
        }
    }

    internal void Finish(byte[] output, int offset)
    {
        ulong bitLen = _totalBytes * 8;

        _buffer[_bufferLen++] = 0x80;
        if (_bufferLen > BlockSize - 16)
        {
            while (_bufferLen < BlockSize)
                _buffer[_bufferLen++] = 0;
            ProcessBlock(_buffer, 0);
            _bufferLen = 0;
        }
        while (_bufferLen < BlockSize - 16)
            _buffer[_bufferLen++] = 0;

        // 128-bit big-endian bit length (top 8 bytes stay zero for
        // messages below 2^64 bits, which covers every Phase 6 use).
        for (int i = 0; i < 8; i++)
            _buffer[BlockSize - 16 + i] = 0;
        for (int i = 0; i < 8; i++)
            _buffer[BlockSize - 8 + i] = (byte)(bitLen >> (56 - i * 8));
        ProcessBlock(_buffer, 0);
        _bufferLen = 0;

        int words = _is384 ? 6 : 8;
        for (int i = 0; i < words; i++)
        {
            ulong v = _state[i];
            for (int b = 0; b < 8; b++)
                output[offset + i * 8 + b] = (byte)(v >> (56 - b * 8));
        }
    }

    private static ulong ReadBE64(byte[] b, int i)
    {
        return ((ulong)b[i] << 56) | ((ulong)b[i + 1] << 48) | ((ulong)b[i + 2] << 40) |
               ((ulong)b[i + 3] << 32) | ((ulong)b[i + 4] << 24) | ((ulong)b[i + 5] << 16) |
               ((ulong)b[i + 6] << 8) | b[i + 7];
    }

    private static readonly ulong[] K = new ulong[80]
    {
        0x428a2f98d728ae22, 0x7137449123ef65cd, 0xb5c0fbcfec4d3b2f, 0xe9b5dba58189dbbc,
        0x3956c25bf348b538, 0x59f111f1b605d019, 0x923f82a4af194f9b, 0xab1c5ed5da6d8118,
        0xd807aa98a3030242, 0x12835b0145706fbe, 0x243185be4ee4b28c, 0x550c7dc3d5ffb4e2,
        0x72be5d74f27b896f, 0x80deb1fe3b1696b1, 0x9bdc06a725c71235, 0xc19bf174cf692694,
        0xe49b69c19ef14ad2, 0xefbe4786384f25e3, 0x0fc19dc68b8cd5b5, 0x240ca1cc77ac9c65,
        0x2de92c6f592b0275, 0x4a7484aa6ea6e483, 0x5cb0a9dcbd41fbd4, 0x76f988da831153b5,
        0x983e5152ee66dfab, 0xa831c66d2db43210, 0xb00327c898fb213f, 0xbf597fc7beef0ee4,
        0xc6e00bf33da88fc2, 0xd5a79147930aa725, 0x06ca6351e003826f, 0x142929670a0e6e70,
        0x27b70a8546d22ffc, 0x2e1b21385c26c926, 0x4d2c6dfc5ac42aed, 0x53380d139d95b3df,
        0x650a73548baf63de, 0x766a0abb3c77b2a8, 0x81c2c92e47edaee6, 0x92722c851482353b,
        0xa2bfe8a14cf10364, 0xa81a664bbc423001, 0xc24b8b70d0f89791, 0xc76c51a30654be30,
        0xd192e819d6ef5218, 0xd69906245565a910, 0xf40e35855771202a, 0x106aa07032bbd1b8,
        0x19a4c116b8d2d0c8, 0x1e376c085141ab53, 0x2748774cdf8eeb99, 0x34b0bcb5e19b48a8,
        0x391c0cb3c5c95a63, 0x4ed8aa4ae3418acb, 0x5b9cca4f7763e373, 0x682e6ff3d6b2b8a3,
        0x748f82ee5defb2fc, 0x78a5636f43172f60, 0x84c87814a1f0ab72, 0x8cc702081a6439ec,
        0x90befffa23631e28, 0xa4506cebde82bde9, 0xbef9a3f7b2c67915, 0xc67178f2e372532b,
        0xca273eceea26619c, 0xd186b8c721c0c207, 0xeada7dd6cde0eb1e, 0xf57d4f7fee6ed178,
        0x06f067aa72176fba, 0x0a637dc5a2c898a6, 0x113f9804bef90dae, 0x1b710b35131c471b,
        0x28db77f523047d84, 0x32caab7b40c72493, 0x3c9ebe0a15c9bebc, 0x431d67c49c100d4c,
        0x4cc5d4becb3e42b6, 0x597f299cfc657e2a, 0x5fcb6fab3ad6faec, 0x6c44198c4a475817,
    };

    private void ProcessBlock(byte[] block, int offset)
    {
        ulong[] w = new ulong[80];
        for (int i = 0; i < 16; i++)
            w[i] = ReadBE64(block, offset + i * 8);
        for (int i = 16; i < 80; i++)
        {
            ulong s0 = Ror(w[i - 15], 1) ^ Ror(w[i - 15], 8) ^ (w[i - 15] >> 7);
            ulong s1 = Ror(w[i - 2], 19) ^ Ror(w[i - 2], 61) ^ (w[i - 2] >> 6);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        ulong a = _state[0], b = _state[1], c = _state[2], d = _state[3];
        ulong e = _state[4], f = _state[5], g = _state[6], h = _state[7];

        for (int i = 0; i < 80; i++)
        {
            ulong S1 = Ror(e, 14) ^ Ror(e, 18) ^ Ror(e, 41);
            ulong ch = (e & f) ^ (~e & g);
            ulong temp1 = h + S1 + ch + K[i] + w[i];
            ulong S0 = Ror(a, 28) ^ Ror(a, 34) ^ Ror(a, 39);
            ulong maj = (a & b) ^ (a & c) ^ (b & c);
            ulong temp2 = S0 + maj;

            h = g;
            g = f;
            f = e;
            e = d + temp1;
            d = c;
            c = b;
            b = a;
            a = temp1 + temp2;
        }

        _state[0] += a;
        _state[1] += b;
        _state[2] += c;
        _state[3] += d;
        _state[4] += e;
        _state[5] += f;
        _state[6] += g;
        _state[7] += h;
    }

    private static ulong Ror(ulong x, int n) => (x >> n) | (x << (64 - n));
}

/// <summary>Managed SHA-512 hash (FIPS 180-4).</summary>
public sealed class Sha512
{
    private readonly Sha512Engine _engine = new Sha512Engine(false);

    /// <summary>Create a new SHA-512 instance with the standard IV.</summary>
    public Sha512() { }

    /// <summary>Digest size in bytes (64).</summary>
    public int DigestSize => 64;

    /// <summary>Reset to the initial state.</summary>
    public void Reset() => _engine.Reset();

    /// <summary>Hash the next block of data.</summary>
    public void Update(byte[] data, int offset, int count) => _engine.Update(data, offset, count);

    /// <summary>Hash the next block of data.</summary>
    public unsafe void Update(byte* data, int count) => _engine.Update(data, count);

    /// <summary>Finish and produce the 64-byte digest.</summary>
    public byte[] Finish()
    {
        byte[] result = new byte[64];
        _engine.Finish(result, 0);
        return result;
    }

    /// <summary>Finish and write the 64-byte digest at <paramref name="offset"/>.</summary>
    public void Finish(byte[] output, int offset) => _engine.Finish(output, offset);

    /// <summary>One-shot SHA-512 over a byte array.</summary>
    public static byte[] Hash(byte[] data)
    {
        var h = new Sha512();
        h.Update(data, 0, data.Length);
        return h.Finish();
    }
}

/// <summary>Managed SHA-384 hash (SHA-512 with the SHA-384 IV, 48-byte output).</summary>
public sealed class Sha384
{
    private readonly Sha512Engine _engine = new Sha512Engine(true);

    /// <summary>Create a new SHA-384 instance with the standard IV.</summary>
    public Sha384() { }

    /// <summary>Digest size in bytes (48).</summary>
    public int DigestSize => 48;

    /// <summary>Reset to the initial state.</summary>
    public void Reset() => _engine.Reset();

    /// <summary>Hash the next block of data.</summary>
    public void Update(byte[] data, int offset, int count) => _engine.Update(data, offset, count);

    /// <summary>Hash the next block of data.</summary>
    public unsafe void Update(byte* data, int count) => _engine.Update(data, count);

    /// <summary>Finish and produce the 48-byte digest.</summary>
    public byte[] Finish()
    {
        byte[] result = new byte[48];
        _engine.Finish(result, 0);
        return result;
    }

    /// <summary>Finish and write the 48-byte digest at <paramref name="offset"/>.</summary>
    public void Finish(byte[] output, int offset) => _engine.Finish(output, offset);

    /// <summary>One-shot SHA-384 over a byte array.</summary>
    public static byte[] Hash(byte[] data)
    {
        var h = new Sha384();
        h.Update(data, 0, data.Length);
        return h.Finish();
    }
}
