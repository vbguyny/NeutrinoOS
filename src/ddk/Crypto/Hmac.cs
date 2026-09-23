// ProtonOS DDK - HMAC (Phase 6)
//
// Managed C# HMAC (RFC 2104) over the Phase 6 hash primitives.
// Used by SSH MAC negotiation (hmac-sha2-256/512), HKDF and SNMP-style
// legacy paths. HMAC-SHA1/MD5 exist for legacy peers only and are
// disabled by default; see docs/PHASE6-CRYPTO.md.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>Hash selection for the HMAC engine.</summary>
public enum HashKind
{
    /// <summary>SHA-1 (legacy; disabled by default in the policy layer).</summary>
    Sha1 = 1,
    /// <summary>SHA-256.</summary>
    Sha256 = 2,
    /// <summary>SHA-384.</summary>
    Sha384 = 3,
    /// <summary>SHA-512.</summary>
    Sha512 = 4,
    /// <summary>MD5 (legacy; disabled by default in the policy layer).</summary>
    Md5 = 5,
}

/// <summary>Managed HMAC over SHA-1/SHA-256/SHA-384/SHA-512/MD5.</summary>
public sealed class Hmac
{
    private readonly HashKind _kind;
    private readonly byte[] _ipadKey;
    private readonly byte[] _opadKey;
    private readonly object _inner;

    /// <summary>Digest size in bytes for the selected hash.</summary>
    public static int DigestSizeFor(HashKind kind)
    {
        switch (kind)
        {
            case HashKind.Sha1: return 20;
            case HashKind.Sha256: return 32;
            case HashKind.Sha384: return 48;
            case HashKind.Sha512: return 64;
            case HashKind.Md5: return 16;
            default: return 0;
        }
    }

    /// <summary>Block size in bytes for the selected hash.</summary>
    public static int BlockSizeFor(HashKind kind)
    {
        switch (kind)
        {
            case HashKind.Sha384:
            case HashKind.Sha512: return 128;
            default: return 64;
        }
    }

    /// <summary>Create an HMAC instance with a byte-array key.</summary>
    public Hmac(HashKind kind, byte[] key)
    {
        _kind = kind;
        int block = BlockSizeFor(kind);

        byte[] k = key;
        if (key.Length > block)
        {
            byte[] hashed = Hash(key);
            k = hashed;
        }

        _ipadKey = new byte[block];
        _opadKey = new byte[block];
        for (int i = 0; i < block; i++)
        {
            byte b = i < k.Length ? k[i] : (byte)0;
            _ipadKey[i] = (byte)(b ^ 0x36);
            _opadKey[i] = (byte)(b ^ 0x5c);
        }

        _inner = NewHash();
        Feed(_inner, _ipadKey, 0, _ipadKey.Length);
    }

    /// <summary>Create an HMAC instance with an unmanaged key buffer.</summary>
    public unsafe Hmac(HashKind kind, byte* key, int keyLength)
    {
        byte[] k = new byte[keyLength];
        for (int i = 0; i < keyLength; i++)
            k[i] = key[i];
        // Delegates to the array constructor logic.
        _kind = kind;
        int block = BlockSizeFor(kind);

        if (k.Length > block)
        {
            byte[] hashed = Hash(k);
            k = hashed;
        }

        _ipadKey = new byte[block];
        _opadKey = new byte[block];
        for (int i = 0; i < block; i++)
        {
            byte b = i < k.Length ? k[i] : (byte)0;
            _ipadKey[i] = (byte)(b ^ 0x36);
            _opadKey[i] = (byte)(b ^ 0x5c);
        }

        _inner = NewHash();
        Feed(_inner, _ipadKey, 0, _ipadKey.Length);
    }

    private byte[] Hash(byte[] data)
    {
        switch (_kind)
        {
            case HashKind.Sha1: return Sha1.Hash(data);
            case HashKind.Sha256: return Sha256.Hash(data);
            case HashKind.Sha384: return Sha384.Hash(data);
            case HashKind.Sha512: return Sha512.Hash(data);
            case HashKind.Md5: return Md5.Hash(data);
            default: return new byte[0];
        }
    }

    private object NewHash()
    {
        switch (_kind)
        {
            case HashKind.Sha1: return new Sha1();
            case HashKind.Sha256: return new Sha256();
            case HashKind.Sha384: return new Sha384();
            case HashKind.Sha512: return new Sha512();
            case HashKind.Md5: return new Md5();
            default: throw new InvalidOperationException("unsupported hash");
        }
    }

    private static void Feed(object hash, byte[] data, int offset, int count)
    {
        if (hash is Sha1 s1) s1.Update(data, offset, count);
        else if (hash is Sha256 s256) s256.Update(data, offset, count);
        else if (hash is Sha384 s384) s384.Update(data, offset, count);
        else if (hash is Sha512 s512) s512.Update(data, offset, count);
        else if (hash is Md5 m5) m5.Update(data, offset, count);
    }

    private static void FinishInto(object hash, byte[] output, int offset)
    {
        if (hash is Sha1 s1) s1.Finish(output, offset);
        else if (hash is Sha256 s256) s256.Finish(output, offset);
        else if (hash is Sha384 s384) s384.Finish(output, offset);
        else if (hash is Sha512 s512) s512.Finish(output, offset);
        else if (hash is Md5 m5) m5.Finish(output, offset);
    }

    /// <summary>Feed message bytes into the HMAC.</summary>
    public void Update(byte[] data, int offset, int count) => Feed(_inner, data, offset, count);

    /// <summary>Feed message bytes into the HMAC.</summary>
    public unsafe void Update(byte* data, int count)
    {
        if (_inner is Sha1 s1) s1.Update(data, count);
        else if (_inner is Sha256 s256) s256.Update(data, count);
        else if (_inner is Sha384 s384) s384.Update(data, count);
        else if (_inner is Sha512 s512) s512.Update(data, count);
        else if (_inner is Md5 m5) m5.Update(data, count);
    }

    /// <summary>Finish and produce the MAC.</summary>
    public byte[] Finish()
    {
        int ds = DigestSizeFor(_kind);

        byte[] innerDigest = new byte[ds];
        FinishInto(_inner, innerDigest, 0);

        object outer = NewHash();
        Feed(outer, _opadKey, 0, _opadKey.Length);
        Feed(outer, innerDigest, 0, innerDigest.Length);

        byte[] mac = new byte[ds];
        FinishInto(outer, mac, 0);
        return mac;
    }

    /// <summary>One-shot HMAC over a byte array.</summary>
    public static byte[] Compute(HashKind kind, byte[] key, byte[] data)
    {
        var h = new Hmac(kind, key);
        h.Update(data, 0, data.Length);
        return h.Finish();
    }

    /// <summary>One-shot HMAC over buffers, writing the MAC to <paramref name="outMac"/>.</summary>
    public static unsafe int Compute(HashKind kind, byte* key, int keyLength,
        byte* data, int dataLength, byte* outMac)
    {
        var h = new Hmac(kind, key, keyLength);
        h.Update(data, dataLength);
        byte[] mac = h.Finish();
        for (int i = 0; i < mac.Length; i++)
            outMac[i] = mac[i];
        return mac.Length;
    }
}
