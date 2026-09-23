// ProtonOS DDK - AES-CTR streaming helper (Phase 6)
//
// Aes.CtrXor restarts from a counter block; SSH needs a CTR stream
// whose keystream position continues across packets for the lifetime
// of a direction (RFC 4344). This helper keeps the counter and the
// offset within the current keystream block.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Continuous AES-CTR keystream (see file header).</summary>
public sealed class AesCtrStream
{
    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _block = new byte[16];
    private int _offset = 16;

    /// <summary>Create a stream from an AES key object and 16-byte IV.</summary>
    public AesCtrStream(Aes aes, byte[] iv)
    {
        _aes = aes;
        for (int i = 0; i < 16; i++)
            _counter[i] = iv[i];
    }

    /// <summary>XOR the keystream into data[offset..offset+length).</summary>
    public void Xor(byte[] data, int offset, int length)
    {
        int pos = 0;
        while (pos < length)
        {
            if (_offset == 16)
            {
                for (int i = 0; i < 16; i++)
                    _block[i] = _counter[i];
                _aes.EncryptBlock(_block, 0);
                // 128-bit big-endian increment.
                for (int i = 15; i >= 0; i--)
                {
                    _counter[i]++;
                    if (_counter[i] != 0)
                        break;
                }
                _offset = 0;
            }

            int n = 16 - _offset;
            if (n > length - pos)
                n = length - pos;
            for (int i = 0; i < n; i++)
                data[offset + pos + i] ^= _block[_offset + i];
            _offset += n;
            pos += n;
        }
    }
}
