// ProtonOS DDK - X.509 / PEM helpers (Phase 6)
//
// Minimal managed X.509 support for the TLS 1.3 server: self-signed
// Ed25519 certificate generation (DER, written from scratch), PEM
// encode/decode, and targeted extraction of the Ed25519 public key /
// private seed. The parser is deliberately minimal - it understands the
// certificates this module generates plus standard OpenSSL Ed25519
// PEM files - and is documented as such in docs/PHASE6-TLS.md.

using System;
using ProtonOS.DDK.Crypto;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Util;

namespace ProtonOS.DDK.Tls;

/// <summary>Minimal X.509 / PEM utilities for the TLS server (see file header).</summary>
public static class X509
{
    /// <summary>OID 1.3.101.112 (Ed25519) DER-encoded.</summary>
    private static readonly byte[] Ed25519Oid = new byte[] { 0x06, 0x03, 0x2B, 0x65, 0x70 };

    // ==================== Certificate generation ====================

    /// <summary>
    /// Builds a self-signed v3 certificate for an Ed25519 key pair.
    /// Validity is [now, now + 10 years] taken from the kernel wall clock
    /// (falling back to 2026-01-01 when the RTC is unset).
    /// </summary>
    public static byte[] BuildSelfSigned(byte[] seed, string commonName,
        string[] dnsNames, string[] ipAddresses)
    {
        byte[] pub = Ed25519.PublicKeyFromSeed(seed);

        var tbs = new Der();
        tbs.Sequence();
        // [0] version = 2 (v3)
        var version = new Der();
        version.IntegerSmall(2);
        tbs.RawWithTag(0xA0, version.ToArray());
        // serial: 8 random bytes, positive (clear top bit)
        var serial = Csprng.GetBytes(8);
        serial[0] = (byte)(serial[0] & 0x7F);
        tbs.Integer(serial);
        // signature algorithm (AlgorithmIdentifier = SEQUENCE { OID })
        tbs.Raw(BuildAlgId());
        // issuer
        tbs.Raw(BuildName(commonName));
        // validity (must precede subject per RFC 5280 TBS order)
        tbs.Raw(BuildValidity());
        // subject
        tbs.Raw(BuildName(commonName));
        // subject public key info
        tbs.Raw(BuildSpki(pub));
        // extensions
        var exts = new Der();
        exts.Sequence();
        exts.Raw(BuildBasicConstraints());
        exts.Raw(BuildKeyUsage());
        exts.Raw(BuildSubjectAltName(dnsNames, ipAddresses));
        exts.EndSequence();
        tbs.RawWithTag(0xA3, exts.ToArray());
        tbs.EndSequence();
        byte[] tbsBytes = tbs.ToArray();

        byte[] signature = Ed25519.Sign(seed, tbsBytes);

        var cert = new Der();
        cert.Sequence();
        cert.Raw(tbsBytes);
        cert.Raw(BuildAlgId());
        cert.BitString(signature);
        cert.EndSequence();
        return cert.ToArray();
    }

    /// <summary>AlgorithmIdentifier for Ed25519: SEQUENCE { OID 1.3.101.112 }.</summary>
    private static byte[] BuildAlgId()
    {
        var d = new Der();
        d.Sequence();
        d.Raw(Ed25519Oid);
        d.EndSequence();
        return d.ToArray();
    }

    private static byte[] BuildName(string commonName)
    {
        byte[] cn = Ascii(commonName);
        var d = new Der();
        d.Sequence();
        d.Raw(BuildRdn(cn));
        d.EndSequence();
        return d.ToArray();
    }

    /// <summary>
    /// Wraps a 32-byte Ed25519 seed in a PKCS#8 PrivateKeyInfo DER blob
    /// (SEQUENCE { INTEGER 0, AlgorithmIdentifier, OCTET STRING { OCTET
    /// STRING seed } }), ready for PEM wrapping.
    /// </summary>
    public static byte[] BuildPkcs8Ed25519(byte[] seed)
    {
        var inner = new Der();
        inner.OctetString(seed);
        var pk = new Der();
        pk.Sequence();
        pk.IntegerSmall(0);
        pk.Raw(Ed25519Oid);
        pk.OctetString(inner.ToArray());
        pk.EndSequence();
        return pk.ToArray();
    }

    private static byte[] BuildRdn(byte[] utf8Value)
    {
        var d = new Der();
        d.Set();
        d.Sequence();
        byte[] cnOid = new byte[] { 0x06, 0x03, 0x55, 0x04, 0x03 };  // 2.5.4.3
        d.Raw(cnOid);
        d.Raw(Tag(0x0C, utf8Value));   // UTF8String
        d.EndSequence();
        d.EndSet();
        return d.ToArray();
    }

    private static byte[] BuildValidity()
    {
        SysInfo.GetWallClock(out int year, out int month, out int day,
            out int hour, out int minute, out int second);
        if (year < 2000 || year > 2100)
        {
            year = 2026;
            month = 1;
            day = 1;
            hour = 0;
            minute = 0;
            second = 0;
        }

        var d = new Der();
        d.Sequence();
        d.Raw(Tag(0x17, Ascii(UtcTime(year, month, day, hour, minute, second))));
        d.Raw(Tag(0x17, Ascii(UtcTime(year + 10, month, day, hour, minute, second))));
        d.EndSequence();
        return d.ToArray();
    }

    private static string UtcTime(int year, int month, int day, int hour, int minute, int second)
    {
        var chars = new char[13];
        chars[0] = (char)('0' + ((year / 10) % 10));
        chars[1] = (char)('0' + (year % 10));
        chars[2] = (char)('0' + (month / 10));
        chars[3] = (char)('0' + (month % 10));
        chars[4] = (char)('0' + (day / 10));
        chars[5] = (char)('0' + (day % 10));
        chars[6] = (char)('0' + (hour / 10));
        chars[7] = (char)('0' + (hour % 10));
        chars[8] = (char)('0' + (minute / 10));
        chars[9] = (char)('0' + (minute % 10));
        chars[10] = (char)('0' + (second / 10));
        chars[11] = (char)('0' + (second % 10));
        chars[12] = 'Z';
        return new string(chars);
    }

    private static byte[] BuildSpki(byte[] pub)
    {
        var d = new Der();
        d.Sequence();
        d.Raw(BuildAlgId());
        d.BitString(pub);
        d.EndSequence();
        return d.ToArray();
    }

    private static byte[] BuildBasicConstraints()
    {
        // Extension { extnID 2.5.29.19, critical false, OCTET STRING { SEQUENCE { } } }
        var d = new Der();
        d.Sequence();
        d.Raw(new byte[] { 0x06, 0x03, 0x55, 0x1D, 0x13 });
        var inner = new Der();
        inner.Sequence();
        inner.EndSequence();
        d.OctetString(inner.ToArray());
        d.EndSequence();
        return d.ToArray();
    }

    private static byte[] BuildKeyUsage()
    {
        // digitalSignature only: BIT STRING (0 unused bits) 07 80
        var d = new Der();
        d.Sequence();
        d.Raw(new byte[] { 0x06, 0x03, 0x55, 0x1D, 0x0F });
        d.Raw(new byte[] { 0x01, 0x01, 0xFF });          // critical TRUE
        d.OctetString(new byte[] { 0x03, 0x02, 0x07, 0x80 });
        d.EndSequence();
        return d.ToArray();
    }

    private static byte[] BuildSubjectAltName(string[] dnsNames, string[] ipAddresses)
    {
        var inner = new Der();
        inner.Sequence();
        if (dnsNames != null)
        {
            for (int i = 0; i < dnsNames.Length; i++)
                inner.Raw(Tag(0x82, Ascii(dnsNames[i])));       // [2] dNSName
        }
        if (ipAddresses != null)
        {
            for (int i = 0; i < ipAddresses.Length; i++)
                inner.Raw(Tag(0x87, ParseIp(ipAddresses[i])));  // [7] iPAddress
        }
        inner.EndSequence();

        var d = new Der();
        d.Sequence();
        d.Raw(new byte[] { 0x06, 0x03, 0x55, 0x1D, 0x11 });
        d.OctetString(inner.ToArray());
        d.EndSequence();
        return d.ToArray();
    }

    private static byte[] ParseIp(string ip)
    {
        var parts = new byte[4];
        int part = 0;
        int value = 0;
        for (int i = 0; i < ip.Length; i++)
        {
            char c = ip[i];
            if (c == '.')
            {
                if (part < 4)
                    parts[part++] = (byte)value;
                value = 0;
            }
            else if (c >= '0' && c <= '9')
            {
                value = value * 10 + (c - '0');
            }
        }
        if (part < 4)
            parts[part] = (byte)value;
        return parts;
    }

    // ==================== PEM ====================

    /// <summary>Wraps DER bytes in a PEM block with 64-column base64.</summary>
    public static string ToPem(byte[] der, string label)
    {
        string b64 = Base64.Encode(der);
        var sb = new char[b64.Length + b64.Length / 64 * 2 + 64];
        int n = 0;
        n = Append(sb, n, "-----BEGIN ");
        n = Append(sb, n, label);
        n = Append(sb, n, "-----\n");
        for (int i = 0; i < b64.Length; i += 64)
        {
            int len = b64.Length - i;
            if (len > 64)
                len = 64;
            for (int j = 0; j < len; j++)
                sb[n++] = b64[i + j];
            sb[n++] = '\n';
        }
        n = Append(sb, n, "-----END ");
        n = Append(sb, n, label);
        n = Append(sb, n, "-----\n");
        var result = new char[n];
        for (int i = 0; i < n; i++)
            result[i] = sb[i];
        return new string(result);
    }

    private static int Append(char[] target, int pos, string s)
    {
        for (int i = 0; i < s.Length; i++)
            target[pos + i] = s[i];
        return pos + s.Length;
    }

    /// <summary>Returns the DER body of the first PEM block, or the input
    /// unchanged when it does not look like PEM (raw DER pass-through).</summary>
    public static byte[] FromPem(string text)
    {
        if (text == null)
            return null;
        int begin = Find(text, "-----BEGIN");
        if (begin < 0)
            return null;
        int afterLabel = Find(text, "-----", begin + 10);
        if (afterLabel < 0)
            return null;
        int bodyStart = afterLabel + 5;
        if (bodyStart < text.Length && text[bodyStart] == '\r')
            bodyStart++;
        if (bodyStart < text.Length && text[bodyStart] == '\n')
            bodyStart++;
        int end = Find(text, "-----END", bodyStart);
        if (end < 0)
            return null;

        // Collect base64 characters only.
        var chars = new char[end - bodyStart];
        int count = 0;
        for (int i = bodyStart; i < end; i++)
        {
            char c = text[i];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')
            {
                chars[count++] = c;
            }
        }
        var b64 = new char[count];
        for (int i = 0; i < count; i++)
            b64[i] = chars[i];
        return Base64.Decode(new string(b64));
    }

    private static int Find(string text, string needle, int start = 0)
    {
        for (int i = start; i + needle.Length <= text.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (text[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
                return i;
        }
        return -1;
    }

    // ==================== Ed25519 extraction ====================

    /// <summary>
    /// Extracts the 32-byte Ed25519 public key from a certificate or SPKI
    /// DER blob: locates the Ed25519 OID and reads the following
    /// BIT STRING (03 21 00). Returns null when not found.
    /// </summary>
    public static byte[] PeelEd25519Public(byte[] der)
    {
        for (int i = 0; i + 7 < der.Length; i++)
        {
            if (der[i] != 0x06 || der[i + 1] != 0x03 ||
                der[i + 2] != 0x2B || der[i + 3] != 0x65 || der[i + 4] != 0x70)
                continue;
            // Look for BIT STRING of 32 bytes with zero unused bits.
            for (int j = i + 5; j + 3 < der.Length; j++)
            {
                if (der[j] == 0x03 && der[j + 1] == 0x21 && der[j + 2] == 0x00)
                {
                    var key = new byte[32];
                    for (int k = 0; k < 32; k++)
                        key[k] = der[j + 3 + k];
                    return key;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts the 32-byte Ed25519 seed from a PKCS#8 DER blob
    /// (the inner OCTET STRING of the privateKey field).
    /// </summary>
    public static byte[] PeelEd25519Seed(byte[] pkcs8)
    {
        if (pkcs8 == null)
            return null;
        // Standard PKCS#8: OCTET STRING(0x22) { OCTET STRING(0x20) { seed } }
        for (int i = 0; i + 37 < pkcs8.Length; i++)
        {
            if (pkcs8[i] == 0x04 && pkcs8[i + 1] == 0x22 &&
                pkcs8[i + 2] == 0x04 && pkcs8[i + 3] == 0x20)
            {
                var seed = new byte[32];
                for (int k = 0; k < 32; k++)
                    seed[k] = pkcs8[i + 4 + k];
                return seed;
            }
        }
        // Fallback: a bare 32-byte OCTET STRING at the tail.
        for (int i = pkcs8.Length - 33; i >= 0; i--)
        {
            if (pkcs8[i] == 0x04 && pkcs8[i + 1] == 0x20 &&
                i + 34 == pkcs8.Length)
            {
                var seed = new byte[32];
                for (int k = 0; k < 32; k++)
                    seed[k] = pkcs8[i + 2 + k];
                return seed;
            }
        }
        return null;
    }

    private static byte[] Ascii(string s)
    {
        var bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
            bytes[i] = (byte)s[i];
        return bytes;
    }

    private static byte[] Tag(byte tag, byte[] body)
    {
        var d = new Der();
        d.RawWithTag(tag, body);
        return d.ToArray();
    }

    // ==================== DER writer ====================

    /// <summary>Minimal DER writer with nested SEQUENCE/SET support
    /// (lengths are always emitted in minimal definite form).</summary>
    private sealed class Der
    {
        private byte[] _buf = new byte[256];
        private int _len;
        private int[] _marks = new int[32];
        private int _depth;

        private void Ensure(int extra)
        {
            if (_len + extra <= _buf.Length)
                return;
            int size = _buf.Length * 2;
            while (size < _len + extra)
                size *= 2;
            var bigger = new byte[size];
            for (int i = 0; i < _len; i++)
                bigger[i] = _buf[i];
            _buf = bigger;
        }

        public void Sequence() => Open(0x30);
        public void Set() => Open(0x31);
        private void Open(byte tag)
        {
            Ensure(2);
            _marks[_depth++] = _len;
            _buf[_len++] = tag;
            _len++;   // length placeholder (short form)
        }

        public void EndSequence() => Close();
        public void EndSet() => Close();
        private void Close()
        {
            int mark = _marks[--_depth];
            int body = _len - mark - 2;
            if (body < 128)
            {
                _buf[mark + 1] = (byte)body;
                return;
            }
            // Long form: compute bytes needed.
            int sizeBytes = body < 256 ? 1 : 2;
            Ensure(sizeBytes);
            for (int i = _len - 1; i >= mark + 2; i--)
                _buf[i + sizeBytes] = _buf[i];
            _len += sizeBytes;
            _buf[mark + 1] = (byte)(0x80 | sizeBytes);
            for (int i = 0; i < sizeBytes; i++)
            {
                int shift = 8 * (sizeBytes - 1 - i);
                _buf[mark + 2 + i] = (byte)(body >> shift);
            }
        }

        public void RawWithTag(byte tag, byte[] body)
        {
            Ensure(2 + body.Length);
            _buf[_len++] = tag;
            WriteLength(body.Length);
            for (int i = 0; i < body.Length; i++)
                _buf[_len++] = body[i];
        }

        public void Raw(byte[] encoded)
        {
            Ensure(encoded.Length);
            for (int i = 0; i < encoded.Length; i++)
                _buf[_len++] = encoded[i];
        }

        public void IntegerSmall(int value)
        {
            var body = new byte[] { (byte)value };
            RawWithTag(0x02, body);
        }

        public void Integer(byte[] bigEndian)
        {
            int start = 0;
            while (start < bigEndian.Length - 1 && bigEndian[start] == 0)
                start++;
            bool pad = (bigEndian[start] & 0x80) != 0;
            var body = new byte[bigEndian.Length - start + (pad ? 1 : 0)];
            int pos = 0;
            if (pad)
                body[pos++] = 0;
            for (int i = start; i < bigEndian.Length; i++)
                body[pos++] = bigEndian[i];
            RawWithTag(0x02, body);
        }

        public void OctetString(byte[] body) => RawWithTag(0x04, body);

        public void BitString(byte[] body)
        {
            var wrapped = new byte[body.Length + 1];
            wrapped[0] = 0x00;   // unused bits
            for (int i = 0; i < body.Length; i++)
                wrapped[i + 1] = body[i];
            RawWithTag(0x03, wrapped);
        }

        public void Tagged(byte tag, byte[] encodedBody) => RawWithTag(tag, encodedBody);

        private void WriteLength(int body)
        {
            Ensure(4);
            if (body < 128)
            {
                _buf[_len++] = (byte)body;
                return;
            }
            if (body < 256)
            {
                _buf[_len++] = 0x81;
                _buf[_len++] = (byte)body;
                return;
            }
            _buf[_len++] = 0x82;
            _buf[_len++] = (byte)(body >> 8);
            _buf[_len++] = (byte)body;
        }

        public byte[] ToArray()
        {
            var result = new byte[_len];
            for (int i = 0; i < _len; i++)
                result[i] = _buf[i];
            return result;
        }
    }
}
