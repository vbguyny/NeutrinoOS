// ProtonOS DDK - MD5 (Phase 6)
//
// Managed C# MD5 (RFC 1321) for legacy SSH compatibility only. MD5 is
// DISABLED by default in the Phase 6 algorithm policy; see
// docs/PHASE6-CRYPTO.md.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Managed MD5 hash (legacy compatibility; see file header).</summary>
public sealed class Md5
{
    private const int BlockSize = 64;

    private readonly uint[] _state;
    private readonly byte[] _buffer;
    private int _bufferLen;
    private ulong _totalBytes;

    /// <summary>Create a new MD5 instance with the standard IV.</summary>
    public Md5()
    {
        _state = new uint[4];
        _buffer = new byte[BlockSize];
        Reset();
    }

    /// <summary>Reset to the initial state.</summary>
    public void Reset()
    {
        _state[0] = 0x67452301;
        _state[1] = 0xefcdab89;
        _state[2] = 0x98badcfe;
        _state[3] = 0x10325476;
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

    /// <summary>Finish and produce the 16-byte digest.</summary>
    public byte[] Finish()
    {
        byte[] result = new byte[16];
        Finish(result, 0);
        return result;
    }

    /// <summary>Finish and write the 16-byte digest at <paramref name="offset"/>.</summary>
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

        // 64-bit little-endian bit length.
        for (int i = 0; i < 8; i++)
            _buffer[BlockSize - 8 + i] = (byte)(bitLen >> (i * 8));
        ProcessBlock(_buffer, 0);
        _bufferLen = 0;

        for (int i = 0; i < 4; i++)
        {
            output[offset + i * 4 + 0] = (byte)_state[i];
            output[offset + i * 4 + 1] = (byte)(_state[i] >> 8);
            output[offset + i * 4 + 2] = (byte)(_state[i] >> 16);
            output[offset + i * 4 + 3] = (byte)(_state[i] >> 24);
        }
    }

    /// <summary>One-shot MD5 over a byte array.</summary>
    public static byte[] Hash(byte[] data)
    {
        var h = new Md5();
        h.Update(data, 0, data.Length);
        return h.Finish();
    }

    private static uint ReadLE32(byte[] b, int i)
    {
        return (uint)b[i] | ((uint)b[i + 1] << 8) | ((uint)b[i + 2] << 16) | ((uint)b[i + 3] << 24);
    }

    // T[i] = floor(2^32 * abs(sin(i + 1)))
    private static readonly uint[] T = new uint[64]
    {
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    };

    private static readonly int[] S = new int[64]
    {
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    };

    private void ProcessBlock(byte[] block, int offset)
    {
        uint[] m = new uint[16];
        for (int i = 0; i < 16; i++)
            m[i] = ReadLE32(block, offset + i * 4);

        uint a = _state[0], b = _state[1], c = _state[2], d = _state[3];

        for (int i = 0; i < 64; i++)
        {
            uint f;
            int g;
            if (i < 16) { f = (b & c) | (~b & d); g = i; }
            else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) % 16; }
            else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) % 16; }
            else { f = c ^ (b | ~d); g = (7 * i) % 16; }

            uint temp = d;
            d = c;
            c = b;
            b = b + Rotl(a + f + T[i] + m[g], S[i]);
            a = temp;
        }

        _state[0] += a;
        _state[1] += b;
        _state[2] += c;
        _state[3] += d;
    }

    private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));
}
