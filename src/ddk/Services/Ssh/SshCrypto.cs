// ProtonOS DDK - SSH cryptography contexts (Phase 6)
//
// Key derivation per RFC 4253 section 7.2 (with the RFC 6668
// encrypt-then-MAC variant) and the per-direction packet cipher/MAC
// state used by the binary packet protocol.

using System;
using ProtonOS.DDK.Crypto;

namespace ProtonOS.DDK.Services.Ssh;

/// <summary>SSH packet cryptography for one direction.</summary>
public sealed class SshPacketCrypto
{
    private AesCtrStream _ctr;
    private readonly byte[] _macKey;
    private readonly HashKind _macKind;
    private readonly int _macLen;
    private readonly bool _etm;
    private uint _seq;

    /// <summary>Create a direction context (null cipher = plaintext).</summary>
    public SshPacketCrypto(byte[] key, byte[] iv, byte[] macKey, HashKind macKind, bool etm)
    {
        if (key != null && iv != null)
            _ctr = new AesCtrStream(new Aes(key), iv);
        _macKey = macKey;
        _macKind = macKind;
        _macLen = macKind == HashKind.Sha512 ? 64 : (macKind == HashKind.Sha1 ? 20 : 32);
        _etm = etm;
        _seq = 0;
    }

    /// <summary>True when this direction encrypts.</summary>
    public bool Encrypting => _ctr != null;

    /// <summary>Size of the MAC appended to each packet (0 = none).</summary>
    public int MacLength => _macKey == null ? 0 : _macLen;

    /// <summary>Encrypt-then-MAC mode.</summary>
    public bool IsEtm => _etm;

    /// <summary>Current sequence number.</summary>
    public uint Sequence => _seq;

    /// <summary>Set the sequence number (sequence space continues across
    /// the plaintext-to-encrypted switch).</summary>
    public void SetSequence(uint seq) => _seq = seq;

    /// <summary>Advance the sequence (both directions count every packet).</summary>
    public void NextSequence() => _seq++;

    /// <summary>XOR the keystream over a buffer.</summary>
    public void Xor(byte[] data, int offset, int length)
    {
        if (_ctr != null)
            _ctr.Xor(data, offset, length);
    }

    /// <summary>Compute the HMAC over seq || data.</summary>
    public byte[] Mac(byte[] data, int offset, int length)
    {
        var input = new byte[4 + length];
        input[0] = (byte)(_seq >> 24);
        input[1] = (byte)(_seq >> 16);
        input[2] = (byte)(_seq >> 8);
        input[3] = (byte)_seq;
        for (int i = 0; i < length; i++)
            input[4 + i] = data[offset + i];
        return Hmac.Compute(_macKind, _macKey, input);
    }

    /// <summary>Verify a truncated MAC against the computed one.</summary>
    public bool VerifyMac(byte[] data, int offset, int length, byte[] stored, int storedOffset)
    {
        var mac = Mac(data, offset, length);
        int diff = 0;
        for (int i = 0; i < _macLen; i++)
            diff |= mac[i] ^ stored[storedOffset + i];
        return diff == 0;
    }
}

/// <summary>SSH algorithm sets and key derivation (see file header).</summary>
public static class SshAlgorithms
{
    /// <summary>All key derivation outputs for one direction pair.</summary>
    public sealed class KeyMaterial
    {
        /// <summary>Client-to-server AES key.</summary>
        public byte[] KeyC2S;
        /// <summary>Server-to-client AES key.</summary>
        public byte[] KeyS2C;
        /// <summary>Client-to-server IV.</summary>
        public byte[] IvC2S;
        /// <summary>Server-to-client IV.</summary>
        public byte[] IvS2C;
        /// <summary>Client-to-server MAC key.</summary>
        public byte[] MacC2S;
        /// <summary>Server-to-client MAC key.</summary>
        public byte[] MacS2C;
    }

    /// <summary>
    /// Derive all keys: K1 = HASH(K || H || letter || session_id),
    /// with K and H encoded as mpint/string, and extension blocks
    /// K2 = HASH(K || H || K1) when more bytes are needed.
    /// </summary>
    public static KeyMaterial Derive(byte[] kMpint, byte[] h, byte[] sessionId,
        int keyLenC2S, int keyLenS2C, int ivLen, int macLenC2S, int macLenS2C)
    {
        var km = new KeyMaterial();
        km.IvC2S = DeriveOne(kMpint, h, (byte)'A', sessionId, ivLen);
        km.IvS2C = DeriveOne(kMpint, h, (byte)'B', sessionId, ivLen);
        km.KeyC2S = DeriveOne(kMpint, h, (byte)'C', sessionId, keyLenC2S);
        km.KeyS2C = DeriveOne(kMpint, h, (byte)'D', sessionId, keyLenS2C);
        km.MacC2S = DeriveOne(kMpint, h, (byte)'E', sessionId, macLenC2S);
        km.MacS2C = DeriveOne(kMpint, h, (byte)'F', sessionId, macLenS2C);
        return km;
    }

    /// <summary>Produce <paramref name="needed"/> key bytes for one letter.</summary>
    public static byte[] DeriveOne(byte[] kMpint, byte[] h, byte letter, byte[] sessionId, int needed)
    {
        // OpenSSH wire-compatible encoding: H and session_id are hashed
        // RAW (kex.c derive_key uses ssh_digest_update on the raw digest
        // and buffer contents), while K is the length-prefixed mpint.
        //   K1 = HASH(mpint(K) || H || letter || session_id)
        //   Kn = HASH(mpint(K) || H || K1 || ... || Kn-1)
        var result = new byte[needed];
        int filled = 0;

        var first = new SshWriter(kMpint.Length + h.Length + sessionId.Length + 16);
        first.WriteString(kMpint);          // mpint: uint32 length + value
        first.WriteRaw(h, 0, h.Length);     // raw H
        first.WriteByte((byte)letter);
        first.WriteRaw(sessionId, 0, sessionId.Length);   // raw session_id
        byte[] block = Sha256.Hash(first.ToArray());
        CopyBlock(block, result, ref filled, needed);

        while (filled < needed)
        {
            var w = new SshWriter(kMpint.Length + h.Length + filled + 16);
            w.WriteString(kMpint);
            w.WriteRaw(h, 0, h.Length);
            w.WriteRaw(result, 0, filled);  // all previous blocks
            block = Sha256.Hash(w.ToArray());
            CopyBlock(block, result, ref filled, needed);
        }
        return result;
    }

    private static void CopyBlock(byte[] block, byte[] result, ref int filled, int needed)
    {
        int n = needed - filled;
        if (n > block.Length)
            n = block.Length;
        for (int i = 0; i < n; i++)
            result[filled + i] = block[i];
        filled += n;
    }

    /// <summary>The exchange hash H (RFC 5656 / RFC 8731).</summary>
    public static byte[] ExchangeHash(byte[] vc, byte[] vs, byte[] ic, byte[] isBuf,
        byte[] ks, byte[] qc, byte[] qs, byte[] kMpint)
    {
        var w = new SshWriter(vc.Length + vs.Length + ic.Length + isBuf.Length +
                              ks.Length + qc.Length + qs.Length + kMpint.Length + 64);
        w.WriteString(vc);
        w.WriteString(vs);
        w.WriteString(ic);
        w.WriteString(isBuf);
        w.WriteString(ks);
        w.WriteString(qc);
        w.WriteString(qs);
        w.WriteString(kMpint);   // mpint encoding: uint32 length + value
        return Sha256.Hash(w.ToArray());
    }

    /// <summary>Encode a raw big-endian value as an mpint byte array
    /// (RFC 4251: strip leading zero bytes, then add one zero byte
    /// back only when the top bit of the first byte is set).</summary>
    public static byte[] MpintEncode(byte[] value)
    {
        int start = 0;
        while (start < value.Length && value[start] == 0)
            start++;
        if (start == value.Length)
            return new byte[0];          // value zero: empty mpint
        bool pad = (value[start] & 0x80) != 0;
        var result = new byte[value.Length - start + (pad ? 1 : 0)];
        int offset = 0;
        if (pad)
            result[offset++] = 0;
        for (int i = start; i < value.Length; i++)
            result[offset + i - start] = value[i];
        return result;
    }
}
