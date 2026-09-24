// NeutrinoOS Phase 6 utility: cryptotest - known-answer tests for the
// managed crypto primitives (src/ddk/Crypto).
//
// Runs standard test vectors (FIPS 180-4, RFC 1321, RFC 2104/4231,
// FIPS 197, SP 800-38A/D, RFC 5869, RFC 8439) and prints one PASS/FAIL
// line per vector. Exit code = number of failures.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Crypto;

namespace NeutrinoOS.Utility.Cryptotest;

/// <summary>The cryptotest utility (see file header).</summary>
public static class Program
{
    private static int _failures;
    private static int _total;

    /// <summary>Entry point; runs every KAT group.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        Console.WriteLine("[cryptotest] NeutrinoOS managed crypto KATs");

        TestHashes();
        TestHmac();
        TestAes();
        TestAead();
        TestHkdf();
        TestX25519();
        TestEd25519();
        TestScrypt();
        TestCsprng();

        Console.Write("[cryptotest] ");
        Console.Write(_failures == 0 ? "PASS " : "FAIL ");
        Console.Write(Util.PadLeft(_total - _failures, 1));
        Console.Write("/");
        Console.Write(Util.PadLeft(_total, 1));
        Console.WriteLine(" vectors");

        return _failures;
    }

    private static void TestHashes()
    {
        Check("SHA1(abc)", Sha1.Hash(Bytes("abc")),
            Hex("a9993e364706816aba3e25717850c26c9cd0d89d"));
        Check("SHA256(abc)", Sha256.Hash(Bytes("abc")),
            Hex("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        Check("SHA256(empty)", Sha256.Hash(new byte[0]),
            Hex("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
        Check("SHA256(two blocks)", Sha256.Hash(Bytes(
            "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")),
            Hex("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"));
        Check("SHA384(abc)", Sha384.Hash(Bytes("abc")),
            Hex("cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed" +
                "8086072ba1e7cc2358baeca134c825a7"));
        Check("SHA512(abc)", Sha512.Hash(Bytes("abc")),
            Hex("ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a" +
                "2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f"));
        Check("MD5(abc)", Md5.Hash(Bytes("abc")),
            Hex("900150983cd24fb0d6963f7d28e17f72"));
    }

    private static void TestHmac()
    {
        // RFC 4231 test case 1.
        byte[] key = new byte[20];
        for (int i = 0; i < 20; i++)
            key[i] = 0x0b;
        byte[] data = Bytes("Hi There");

        Check("HMAC-SHA256(rfc4231-1)", ProtonOS.DDK.Crypto.Hmac.Compute(HashKind.Sha256, key, data),
            Hex("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"));
        Check("HMAC-SHA512(rfc4231-1)", ProtonOS.DDK.Crypto.Hmac.Compute(HashKind.Sha512, key, data),
            Hex("87aa7cdea5ef619d4ff0b4241a1d6cb02379f4e2ce4ec2787ad0b30545e17cde" +
                "daa833b7d6b8a702038b274eaea3f4e4be9d914eeb61f1702e696c203a126854"));
        // RFC 2202 test case 1.
        Check("HMAC-SHA1(rfc2202-1)", ProtonOS.DDK.Crypto.Hmac.Compute(HashKind.Sha1, key, data),
            Hex("b617318655057264e28bc0b6fb378c8ef146be00"));
    }

    private static void TestAes()
    {
        // FIPS 197 Appendix C.
        var aes128 = new Aes(Hex("000102030405060708090a0b0c0d0e0f"));
        byte[] block = Hex("00112233445566778899aabbccddeeff");
        aes128.EncryptBlock(block, 0);
        Check("AES-128(ecb)", block, Hex("69c4e0d86a7b0430d8cdb78070b4c55a"));

        var aes256 = new Aes(Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));
        byte[] block2 = Hex("00112233445566778899aabbccddeeff");
        aes256.EncryptBlock(block2, 0);
        Check("AES-256(ecb)", block2, Hex("8ea2b7ca516745bfeafc49904b496089"));

        // SP 800-38A F.5.1 CTR-AES128.
        var ctr = new Aes(Hex("2b7e151628aed2a6abf7158809cf4f3c"));
        byte[] data = Hex("6bc1bee22e409f96e93d7e117393172a");
        byte[] counter = Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        unsafe
        {
            fixed (byte* dp = data)
            fixed (byte* cp = counter)
            {
                ctr.CtrXor(dp, data.Length, cp);
            }
        }
        Check("AES-128-CTR(38a)", data, Hex("874d6191b620e3261bef6864990db6ce"));
    }

    private static void TestAead()
    {
        // RFC 8439 section 2.5.2 Poly1305.
        Check("Poly1305(rfc8439)",
            Poly1305.Compute(
                Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b"),
                Bytes("Cryptographic Forum Research Group"), 0, 34),
            Hex("a8061dc1305136c6c22b8baf0c0127a9"));

        // RFC 8439 section 2.8.2 ChaCha20-Poly1305 AEAD.
        var cc = new ChaCha20Poly1305(Hex(
            "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f"));
        byte[] nonce = Hex("070000004041424344454647");
        byte[] aad = Hex("50515253c0c1c2c3c4c5c6c7");
        byte[] pt = Bytes("Ladies and Gentlemen of the class of '99: If I could offer you " +
                          "only one tip for the future, sunscreen would be it.");
        byte[] sealedData = cc.Seal(nonce, aad, pt);
        byte[] expCt = Hex(
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6" +
            "3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36" +
            "92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc" +
            "3ff4def08e4b7a9de576d26586cec64b6116");
        byte[] gotCt = new byte[pt.Length];
        for (int i = 0; i < pt.Length; i++)
            gotCt[i] = sealedData[i];
        Check("ChaCha20Poly1305(ct)", gotCt, expCt);

        byte[] gotTag = new byte[16];
        for (int i = 0; i < 16; i++)
            gotTag[i] = sealedData[pt.Length + i];
        Check("ChaCha20Poly1305(tag)", gotTag, Hex("1ae10b594f09e26a7e902ecbd0600691"));

        // SP 800-38D test case 3: AES-128-GCM, empty plaintext.
        var gcm = new AesGcm(new byte[16]);
        byte[] sealedEmpty = gcm.Seal(new byte[12], null, new byte[0]);
        byte[] emptyTag = new byte[16];
        for (int i = 0; i < 16; i++)
            emptyTag[i] = sealedEmpty[i];
        Check("AES-128-GCM(empty tag)", emptyTag, Hex("58e2fccefa7e3061367f1d57a4e7455a"));

        // SP 800-38D test case 3: 16 zero bytes.
        byte[] sealed16 = gcm.Seal(new byte[12], null, new byte[16]);
        byte[] ct16 = new byte[16];
        byte[] tag16 = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            ct16[i] = sealed16[i];
            tag16[i] = sealed16[16 + i];
        }
        Check("AES-128-GCM(ct)", ct16, Hex("0388dace60b6a392f328c2b971b2fe78"));
        Check("AES-128-GCM(tag)", tag16, Hex("ab6e47d42cec13bdf53a67b21257bddf"));
    }

    private static void TestHkdf()
    {
        // RFC 5869 test case 1.
        byte[] ikm = new byte[22];
        for (int i = 0; i < 22; i++)
            ikm[i] = 0x0b;
        byte[] salt = Hex("000102030405060708090a0b0c");
        byte[] info = Hex("f0f1f2f3f4f5f6f7f8f9");
        byte[] okm = Hkdf.Derive(HashKind.Sha256, salt, ikm, info, 42);
        Check("HKDF(rfc5869-1)", okm, Hex(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf" +
            "34007208d5b887185865"));
    }

    private static void TestX25519()
    {
        // RFC 7748 section 5.2.
        var k1 = Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4");
        var u1 = Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");
        Check("X25519(rfc7748-1)", X25519.ScalarMult(k1, u1),
            Hex("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552"));

        var k2 = Hex("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d");
        var u2 = Hex("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493");
        Check("X25519(rfc7748-2)", X25519.ScalarMult(k2, u2),
            Hex("95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957"));

        var baseU = new byte[32];
        baseU[0] = 9;
        var it = new byte[32];
        for (int i = 0; i < 32; i++)
            it[i] = baseU[i];
        Check("X25519(iter1)", X25519.ScalarMult(it, baseU),
            Hex("422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079"));
    }

    private static void TestEd25519()
    {
        // RFC 8032 section 7.1 test 1.
        var seed = Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        Check("Ed25519(pub)", Ed25519.PublicKeyFromSeed(seed),
            Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"));
        var msg = new byte[0];
        var sig = Ed25519.Sign(seed, msg);
        Check("Ed25519(sig)", sig,
            Hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
                "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"));
        bool ok = Ed25519.Verify(Ed25519.PublicKeyFromSeed(seed), msg, sig);
        bool bad = Ed25519.Verify(Ed25519.PublicKeyFromSeed(seed), new byte[] { 1 }, sig);
        Check("Ed25519(verify)", ok && !bad ? new byte[] { 1 } : new byte[] { 0 }, new byte[] { 1 });
    }

    private static void TestScrypt()
    {
        // RFC 7914 section 12.
        Check("scrypt(rfc7914-1)", Scrypt.Derive(new byte[0], new byte[0], 16, 1, 1, 64),
            Hex("77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442" +
                "fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906"));

        // Password hash round-trip with small parameters (fast).
        var pw = Bytes("hunter2");
        var salt = Hex("00112233445566778899aabbccddeeff");
        string hash = Scrypt.HashPasswordWithParams(pw, salt, 2, 1, 1);
        bool ok = Scrypt.VerifyPassword(pw, hash);
        bool bad = Scrypt.VerifyPassword(Bytes("hunter3"), hash);
        Check("scrypt(pwhash)", ok && !bad ? new byte[] { 1 } : new byte[] { 0 }, new byte[] { 1 });
    }

    private static void TestCsprng()
    {
        // Determinism + entropy mixing.
        var e1 = Hex("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
        var e2 = Hex("ffeeddccbbaa99887766554433221100ffeeddccbbaa99887766554433221100");

        Csprng.DebugReset();
        Csprng.AddEntropy(e1);
        var a = Csprng.GetBytes(48);

        Csprng.DebugReset();
        Csprng.AddEntropy(e1);
        var b = Csprng.GetBytes(48);
        Check("Csprng(repeatable)", a, b);

        Csprng.DebugReset();
        Csprng.AddEntropy(e1);
        Csprng.AddEntropy(e2);
        var c = Csprng.GetBytes(48);
        bool differs = false;
        for (int i = 0; i < 48; i++)
        {
            if (a[i] != c[i])
            {
                differs = true;
                break;
            }
        }
        Check("Csprng(mixes entropy)", differs ? new byte[] { 1 } : new byte[] { 0 }, new byte[] { 1 });
        Csprng.DebugReset();
    }

    private static void Check(string name, byte[] got, byte[] expected)
    {
        _total++;
        bool ok = got.Length == expected.Length;
        if (ok)
        {
            for (int i = 0; i < got.Length; i++)
            {
                if (got[i] != expected[i])
                {
                    ok = false;
                    break;
                }
            }
        }

        if (!ok)
            _failures++;

        Console.Write("  [");
        Console.Write(ok ? "PASS" : "FAIL");
        Console.Write("] ");
        Console.Write(name);
        if (!ok)
        {
            Console.Write("  got=");
            Console.Write(HexString(got));
            Console.Write(" exp=");
            Console.Write(HexString(expected));
        }
        Console.WriteLine();
    }

    private static byte[] Bytes(string s)
    {
        byte[] b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
            b[i] = (byte)s[i];
        return b;
    }

    private static byte[] Hex(string hex)
    {
        int n = hex.Length / 2;
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = (byte)((Nibble(hex[i * 2]) << 4) | Nibble(hex[i * 2 + 1]));
        }
        return b;
    }

    private static int Nibble(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'a' && c <= 'f')
            return c - 'a' + 10;
        if (c >= 'A' && c <= 'F')
            return c - 'A' + 10;
        return 0;
    }

    private static string HexString(byte[] data)
    {
        string s = "";
        for (int i = 0; i < data.Length; i++)
        {
            int hi = data[i] >> 4;
            int lo = data[i] & 15;
            s += HexDigits[hi];
            s += HexDigits[lo];
        }
        return s;
    }

    private static readonly string[] HexDigits =
    {
        "0", "1", "2", "3", "4", "5", "6", "7",
        "8", "9", "a", "b", "c", "d", "e", "f",
    };
}
