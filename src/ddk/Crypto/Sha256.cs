// ProtonOS DDK - SHA-256 (Phase 6)
//
// Managed C# SHA-256 (FIPS 180-4), no native dependencies. Used by
// HMAC-SHA256, TLS 1.3, SSH (curve25519-sha256, hmac-sha2-256) and the
// user database KDF chain. Byte-order helpers are open-coded so the
// implementation only needs the most basic BCL surface (it runs under
// the Tier-0 JIT in the shared DDK assembly).

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed SHA-256 hash (see file header).</summary>
public sealed class Sha256
{
    private const int BlockSize = 64;

    private readonly uint[] _state;
    private readonly byte[] _buffer;
    private int _bufferLen;
    private ulong _totalBytes;

    /// <summary>Create a new SHA-256 instance with the standard IV.</summary>
    public Sha256()
    {
        _state = new uint[8];
        _buffer = new byte[BlockSize];
        Reset();
    }

    /// <summary>Reset to the initial state.</summary>
    public void Reset()
    {
        _state[0] = 0x6a09e667;
        _state[1] = 0xbb67ae85;
        _state[2] = 0x3c6ef372;
        _state[3] = 0xa54ff53a;
        _state[4] = 0x510e527f;
        _state[5] = 0x9b05688c;
        _state[6] = 0x1f83d9ab;
        _state[7] = 0x5be0cd19;
        _bufferLen = 0;
        _totalBytes = 0;
    }

    /// <summary>Hash the next block of data.</summary>
    public void Update(byte[] data, int offset, int count)
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

    /// <summary>Hash the next block of data.</summary>
    public unsafe void Update(byte* data, int count)
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

    /// <summary>Finish and produce the 32-byte digest.</summary>
    public byte[] Finish()
    {
        byte[] result = new byte[32];
        Finish(result, 0);
        return result;
    }

    /// <summary>Finish and write the 32-byte digest at <paramref name="offset"/>.</summary>
    public void Finish(byte[] output, int offset)
    {
        ulong bitLen = _totalBytes * 8;

        // Padding: 0x80, zeros, 8-byte big-endian bit length.
        _buffer[_bufferLen++] = 0x80;
        if (_bufferLen > BlockSize - 8)
        {
            while (_bufferLen < BlockSize)
                _buffer[_bufferLen++] = 0;
            ProcessBlock(_buffer, 0);
            _bufferLen = 0;
        }
        while (_bufferLen < BlockSize - 8)
            _buffer[_bufferLen++] = 0;

        WriteBE64(_buffer, BlockSize - 8, bitLen);
        ProcessBlock(_buffer, 0);
        _bufferLen = 0;

        for (int i = 0; i < 8; i++)
        {
            output[offset + i * 4 + 0] = (byte)(_state[i] >> 24);
            output[offset + i * 4 + 1] = (byte)(_state[i] >> 16);
            output[offset + i * 4 + 2] = (byte)(_state[i] >> 8);
            output[offset + i * 4 + 3] = (byte)_state[i];
        }
    }

    /// <summary>One-shot SHA-256 over a byte array.</summary>
    public static byte[] Hash(byte[] data)
    {
        var h = new Sha256();
        h.Update(data, 0, data.Length);
        return h.Finish();
    }

    /// <summary>One-shot SHA-256 over a buffer, writing 32 bytes out.</summary>
    public static unsafe void Hash(byte* data, int length, byte* outHash)
    {
        var h = new Sha256();
        h.Update(data, length);
        byte[] tmp = new byte[32];
        h.Finish(tmp, 0);
        for (int i = 0; i < 32; i++)
            outHash[i] = tmp[i];
    }

    private static uint ReadBE32(byte[] b, int i)
    {
        return ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
    }

    private static void WriteBE64(byte[] b, int i, ulong v)
    {
        b[i + 0] = (byte)(v >> 56);
        b[i + 1] = (byte)(v >> 48);
        b[i + 2] = (byte)(v >> 40);
        b[i + 3] = (byte)(v >> 32);
        b[i + 4] = (byte)(v >> 24);
        b[i + 5] = (byte)(v >> 16);
        b[i + 6] = (byte)(v >> 8);
        b[i + 7] = (byte)v;
    }

    private static readonly uint[] K = new uint[64]
    {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    };

    private void ProcessBlock(byte[] block, int offset)
    {
        uint[] w = new uint[64];
        for (int i = 0; i < 16; i++)
            w[i] = ReadBE32(block, offset + i * 4);
        for (int i = 16; i < 64; i++)
        {
            uint s0 = Ror(w[i - 15], 7) ^ Ror(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint s1 = Ror(w[i - 2], 17) ^ Ror(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        uint a = _state[0], b = _state[1], c = _state[2], d = _state[3];
        uint e = _state[4], f = _state[5], g = _state[6], h = _state[7];

        for (int i = 0; i < 64; i++)
        {
            uint S1 = Ror(e, 6) ^ Ror(e, 11) ^ Ror(e, 25);
            uint ch = (e & f) ^ (~e & g);
            uint temp1 = h + S1 + ch + K[i] + w[i];
            uint S0 = Ror(a, 2) ^ Ror(a, 13) ^ Ror(a, 22);
            uint maj = (a & b) ^ (a & c) ^ (b & c);
            uint temp2 = S0 + maj;

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

    private static uint Ror(uint x, int n) => (x >> n) | (x << (32 - n));
}
