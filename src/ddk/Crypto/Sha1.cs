// ProtonOS DDK - SHA-1 (Phase 6)
//
// Managed C# SHA-1 (FIPS 180-4) for legacy SSH compatibility
// (diffie-hellman-group14-sha1 era clients, hmac-sha1). SHA-1 is
// DISABLED by default in the Phase 6 algorithm policy except where a
// peer explicitly negotiates it; see docs/PHASE6-CRYPTO.md.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed SHA-1 hash (legacy compatibility; see file header).</summary>
public sealed class Sha1
{
    private const int BlockSize = 64;

    private readonly uint[] _state;
    private readonly byte[] _buffer;
    private int _bufferLen;
    private ulong _totalBytes;

    /// <summary>Create a new SHA-1 instance with the standard IV.</summary>
    public Sha1()
    {
        _state = new uint[5];
        _buffer = new byte[BlockSize];
        Reset();
    }

    /// <summary>Reset to the initial state.</summary>
    public void Reset()
    {
        _state[0] = 0x67452301;
        _state[1] = 0xEFCDAB89;
        _state[2] = 0x98BADCFE;
        _state[3] = 0x10325476;
        _state[4] = 0xC3D2E1F0;
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

    /// <summary>Finish and produce the 20-byte digest.</summary>
    public byte[] Finish()
    {
        byte[] result = new byte[20];
        Finish(result, 0);
        return result;
    }

    /// <summary>Finish and write the 20-byte digest at <paramref name="offset"/>.</summary>
    public void Finish(byte[] output, int offset)
    {
        ulong bitLen = _totalBytes * 8;

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

        // 64-bit big-endian bit length.
        for (int i = 0; i < 8; i++)
            _buffer[BlockSize - 8 + i] = (byte)(bitLen >> (56 - i * 8));
        ProcessBlock(_buffer, 0);
        _bufferLen = 0;

        for (int i = 0; i < 5; i++)
        {
            output[offset + i * 4 + 0] = (byte)(_state[i] >> 24);
            output[offset + i * 4 + 1] = (byte)(_state[i] >> 16);
            output[offset + i * 4 + 2] = (byte)(_state[i] >> 8);
            output[offset + i * 4 + 3] = (byte)_state[i];
        }
    }

    /// <summary>One-shot SHA-1 over a byte array.</summary>
    public static byte[] Hash(byte[] data)
    {
        var h = new Sha1();
        h.Update(data, 0, data.Length);
        return h.Finish();
    }

    private static uint ReadBE32(byte[] b, int i)
    {
        return ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
    }

    private void ProcessBlock(byte[] block, int offset)
    {
        uint[] w = new uint[80];
        for (int i = 0; i < 16; i++)
            w[i] = ReadBE32(block, offset + i * 4);
        for (int i = 16; i < 80; i++)
        {
            uint v = w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16];
            w[i] = (v << 1) | (v >> 31);
        }

        uint a = _state[0], b = _state[1], c = _state[2], d = _state[3], e = _state[4];

        for (int i = 0; i < 80; i++)
        {
            uint f, k;
            if (i < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
            else if (i < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
            else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
            else { f = b ^ c ^ d; k = 0xCA62C1D6; }

            uint temp = Rotl(a, 5) + f + e + k + w[i];
            e = d;
            d = c;
            c = Rotl(b, 30);
            b = a;
            a = temp;
        }

        _state[0] += a;
        _state[1] += b;
        _state[2] += c;
        _state[3] += d;
        _state[4] += e;
    }

    private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));
}
