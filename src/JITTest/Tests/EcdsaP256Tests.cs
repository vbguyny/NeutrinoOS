// JITTest - ECDSA P-256 vectors (DDK EcdsaP256 under the Tier-0 JIT)
//
// The vectors were computed and independently verified in
// build/tmp-p256-constants.py (pure-python P-256): the sign vector
// pins the DER signature byte for byte with a fixed nonce, and the
// round-trips exercise the CSPRNG nonce path on-device.

namespace JITTest;

using NeutrinoOS.DDK.Crypto;

/// <summary>ECDSA P-256 math vectors (see file header).</summary>
public static class EcdsaP256Tests
{
    public static void RunAll()
    {
        string gHex =
            "046b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296" +
            "4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5";
        string g2Hex =
            "047cf27b188d034f7e8a52380304b51ac3c08969e277f21b35a60b48fc47669978" +
            "07775510db8ed040293d9ac69f7430dbba7dade63ce982299e04b79d227873d1";
        string dHex = "519B423D715F8B581F4FA8EE59F4771A5B44C8130B4E3EACCA54A56DDA72B464";
        string qHex =
            "041ccbe91c075fc7f4f033bfa248db8fccd3565de94bbfb12f3c59ff46c271bf83" +
            "ce4014c68811f9a21a1fdb2c0e6113e06db7ca93b7404e78dc7ccd5ca89a4ca9";
        string kHex = "7A1A0F0A9E3D0C0BAA99887766554433221100FFEEDDCCBBAA99887766554433";
        string digestHex = "4980F152412E99B9FFDFA7CD9A19C1C4F656515FB34A9DAD4427EA0F788A449A";
        string derHex =
            "30440220390e67990825e65e407aeda4183eb6a7218c2f04750e71b6df1210c24b9c3c34" +
            "022030c3032fbf61312db6aad0de2e8d61064c9ba09bb9bea21b0f877b14a83687d1";

        string oneHex = "0000000000000000000000000000000000000000000000000000000000000001";
        string twoHex = "0000000000000000000000000000000000000000000000000000000000000002";

        TestTracker.Record("ecdsa/p256-pub-one-is-g",
            HexString(EcdsaP256.PublicKeyFromPrivate(HexBytes(oneHex))) == gHex);
        TestTracker.Record("ecdsa/p256-pub-two-is-2g",
            HexString(EcdsaP256.PublicKeyFromPrivate(HexBytes(twoHex))) == g2Hex);
        TestTracker.Record("ecdsa/p256-pub-vector",
            HexString(EcdsaP256.PublicKeyFromPrivate(HexBytes(dHex))) == qHex);

        byte[] der = EcdsaP256.SignWithK(HexBytes(dHex), HexBytes(digestHex), HexBytes(kHex));
        TestTracker.Record("ecdsa/p256-sign-vector-der", HexString(der) == derHex);

        TestTracker.Record("ecdsa/p256-verify-vector",
            EcdsaP256.Verify(HexBytes(qHex), HexBytes(digestHex), HexBytes(derHex)));

        string tampered = "5980F152412E99B9FFDFA7CD9A19C1C4F656515FB34A9DAD4427EA0F788A449A";
        TestTracker.Record("ecdsa/p256-verify-tampered-false",
            !EcdsaP256.Verify(HexBytes(qHex), HexBytes(tampered), HexBytes(derHex)));

        // CSPRNG path: generate a key, sign, verify (also proves the
        // random-nonce retry loop terminates on-device).
        byte[] priv = EcdsaP256.GeneratePrivateKey();
        bool ok = priv != null;
        if (ok)
        {
            byte[] pub = EcdsaP256.PublicKeyFromPrivate(priv);
            byte[] sig = EcdsaP256.Sign(priv, HexBytes(digestHex));
            ok = sig != null && EcdsaP256.Verify(pub, HexBytes(digestHex), sig);
        }
        TestTracker.Record("ecdsa/p256-csprng-roundtrip", ok);
    }

    private static byte[] HexBytes(string s)
    {
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++)
        {
            int hi = HexVal(s[i * 2]);
            int lo = HexVal(s[i * 2 + 1]);
            b[i] = (byte)((hi << 4) | lo);
        }
        return b;
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'a' && c <= 'f')
            return c - 'a' + 10;
        return c - 'A' + 10;
    }

    private static string HexString(byte[] b)
    {
        if (b == null)
            return "(null)";
        const string digits = "0123456789abcdef";
        var chars = new char[b.Length * 2];
        for (int i = 0; i < b.Length; i++)
        {
            chars[i * 2] = digits[b[i] >> 4];
            chars[i * 2 + 1] = digits[b[i] & 15];
        }
        return new string(chars);
    }
}
