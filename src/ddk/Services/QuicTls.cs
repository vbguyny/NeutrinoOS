// ProtonOS Phase 9: TLS 1.3 handshake driver for QUIC (RFC 9001).
//
// QUIC carries handshake messages in CRYPTO frames instead of TLS
// records, so the record-layer-oriented Tls13Connection cannot be
// reused. This driver performs the server-side handshake and exposes
// the four QUIC encryption levels' keys (initial / handshake / 1-RTT,
// per direction) plus the CRYPTO payloads the QUIC layer must ship.
//
// Crypto primitives are the same DDK building blocks used by the TLS
// server: X25519, HKDF-SHA256, HMAC, Ed25519, SHA-256.
//
// Tier-0 JIT note: every message builder below is a SMALL method. The
// first version assembled the whole server flight in one giant frame and
// the local length counters were silently lost (en/cn/vn/hn read back as
// garbage), producing near-empty messages.
using System;
using ProtonOS.DDK.Crypto;
using ProtonOS.DDK.Kernel;

namespace ProtonOS.DDK.Services;

public sealed class QuicTlsServer
{
    // Application-layer protocol negotiation target.
    public const string Alpn = "h3";

    // ---- derived keys -------------------------------------------------
    public byte[] ClientInitialKey = new byte[16];
    public byte[] ClientInitialIv = new byte[12];
    public byte[] ClientInitialHp = new byte[16];
    public byte[] ServerInitialKey = new byte[16];
    public byte[] ServerInitialIv = new byte[12];
    public byte[] ServerInitialHp = new byte[16];
    public byte[] ServerHsKey = new byte[16];
    public byte[] ServerHsIv = new byte[12];
    public byte[] ServerHsHp = new byte[16];
    public byte[] ClientHsKey = new byte[16];
    public byte[] ClientHsIv = new byte[12];
    public byte[] ClientHsHp = new byte[16];
    public byte[] ServerAppKey = new byte[16];
    public byte[] ServerAppIv = new byte[12];
    public byte[] ServerAppHp = new byte[16];
    public byte[] ClientAppKey = new byte[16];
    public byte[] ClientAppIv = new byte[12];
    public byte[] ClientAppHp = new byte[16];

    /// <summary>True once the client Finished was verified.</summary>
    public bool HandshakeComplete;

    /// <summary>ALPN selected (always "h3" when the client offered it).</summary>
    public string AlpnSelected;

    /// <summary>CRYPTO bytes to send at the Initial level (ServerHello).</summary>
    public byte[] OutInitial = new byte[2048];
    public int OutInitialLen;

    /// <summary>CRYPTO bytes to send at the Handshake level (EE..Finished).</summary>
    public byte[] OutHandshake = new byte[8192];
    public int OutHandshakeLen;

    // ---- state --------------------------------------------------------
    private byte[] _transcript = new byte[8192];
    private int _transcriptLen;
    private readonly byte[] _certDer;
    private readonly byte[] _keySeed;
    private byte[] _clientHsTraffic;
    private byte[] _serverHsTraffic;
    private byte[] _clientApTraffic;
    private byte[] _serverApTraffic;

    // ClientHello echo fields.
    private byte[] _sessionId = new byte[32];
    private int _sessionIdLen;
    private byte[] _clientPub = new byte[32];

    public QuicTlsServer(byte[] certDer, byte[] keySeed)
    {
        _certDer = certDer;
        _keySeed = keySeed;
    }

    // ==================== initial keys ====================

    /// <summary>
    /// Derive the client/server Initial keys from the connection ID the
    /// client used in its first Initial packet (RFC 9001 5.2).
    /// </summary>
    public void InitInitialKeys(byte[] dcid, int off, int len)
    {
        // v1 initial salt.
        byte[] salt = new byte[]
        {
            0x38, 0x76, 0x2c, 0xf7, 0xf5, 0x59, 0x34, 0xb3, 0x4d, 0x17,
            0x9a, 0xe6, 0xa4, 0xc8, 0x0c, 0xad, 0xcc, 0xbb, 0x7f, 0x0a,
        };
        byte[] initialSecret = Hkdf.Extract(HashKind.Sha256, salt, dcid, off, len);
        byte[] clientSecret = QuicExpand(initialSecret, "client in", 32);
        byte[] serverSecret = QuicExpand(initialSecret, "server in", 32);
        DerivePacketKeys(clientSecret, ClientInitialKey, ClientInitialIv, ClientInitialHp);
        DerivePacketKeys(serverSecret, ServerInitialKey, ServerInitialIv, ServerInitialHp);
    }

    private static void DerivePacketKeys(byte[] secret, byte[] key, byte[] iv, byte[] hp)
    {
        byte[] k = QuicExpand(secret, "quic key", 16);
        byte[] i = QuicExpand(secret, "quic iv", 12);
        byte[] h = QuicExpand(secret, "quic hp", 16);
        for (int n = 0; n < 16; n++)
            key[n] = k[n];
        for (int n = 0; n < 12; n++)
            iv[n] = i[n];
        for (int n = 0; n < 16; n++)
            hp[n] = h[n];
    }

    /// <summary>HKDF-Expand-Label with the TLS 1.3 prefix (RFC 8446 7.1).</summary>
    public static byte[] QuicExpand(byte[] secret, string label, int length)
    {
        var info = new byte[2 + 1 + 6 + label.Length + 1];
        int pos = 0;
        info[pos++] = (byte)(length >> 8);
        info[pos++] = (byte)length;
        info[pos++] = (byte)(6 + label.Length);
        byte[] prefix = new byte[] { (byte)'t', (byte)'l', (byte)'s', (byte)'1', (byte)'3', (byte)' ' };
        for (int i = 0; i < 6; i++)
            info[pos++] = prefix[i];
        for (int i = 0; i < label.Length; i++)
            info[pos++] = (byte)label[i];
        info[pos++] = 0;
        return Hkdf.Expand(HashKind.Sha256, secret, info, length);
    }

    // ==================== handshake ====================

    private void AppendT(byte[] data, int off, int len)
    {
        if (_transcriptLen + len > _transcript.Length)
        {
            var bigger = new byte[(_transcriptLen + len) * 2];
            for (int i = 0; i < _transcriptLen; i++)
                bigger[i] = _transcript[i];
            _transcript = bigger;
        }
        for (int i = 0; i < len; i++)
            _transcript[_transcriptLen++] = data[off + i];
    }

    private byte[] HashT()
    {
        var data = new byte[_transcriptLen];
        for (int i = 0; i < _transcriptLen; i++)
            data[i] = _transcript[i];
        return Sha256.Hash(data);
    }

    // ---- ClientHello parsing (returns 0 ok, else error code) ----------

    private int ParseClientHello(byte[] data, int off, int len)
    {
        if (len < 4)
            return 1;
        int bodyLen = (data[off + 1] << 16) | (data[off + 2] << 8) | data[off + 3];
        if (data[off] != 1 || len < 4 + bodyLen)
            return 2;
        int p = off + 4;
        p += 2;                      // legacy_version
        p += 32;                     // random
        int sidLen = data[p++];
        if (sidLen > 32)
            return 3;
        _sessionIdLen = sidLen;
        for (int i = 0; i < sidLen; i++)
            _sessionId[i] = data[p + i];
        p += sidLen;
        int suitesLen = (data[p] << 8) | data[p + 1];
        p += 2;
        int suitesEnd = p + suitesLen;
        bool has1301 = false;
        while (p + 1 < suitesEnd)
        {
            if (data[p] == 0x13 && data[p + 1] == 0x01)
                has1301 = true;
            p += 2;
        }
        p = suitesEnd;
        int compLen = data[p++];
        p += compLen;
        int extTotal = (data[p] << 8) | data[p + 1];
        p += 2;
        int extEnd = p + extTotal;

        bool versionOk = false;
        bool sigOk = false;
        bool sawH3 = false;
        bool hasPub = false;
        while (p + 4 <= extEnd)
        {
            int extType = (data[p] << 8) | data[p + 1];
            int extLen = (data[p + 2] << 8) | data[p + 3];
            int q = p + 4;
            if (extType == 43)              // supported_versions
            {
                int listLen = data[q];
                for (int i = 0; i + 1 < listLen; i += 2)
                {
                    int v = (data[q + 1 + i] << 8) | data[q + 2 + i];
                    if (v == 0x0304)
                        versionOk = true;
                }
            }
            else if (extType == 51)         // key_share
            {
                int sharesLen = (data[q] << 8) | data[q + 1];
                int r = q + 2;
                int sharesEnd = r + sharesLen;
                while (r + 4 <= sharesEnd)
                {
                    int group = (data[r] << 8) | data[r + 1];
                    int klen = (data[r + 2] << 8) | data[r + 3];
                    r += 4;
                    if (group == 0x001D && klen == 32)
                    {
                        for (int i = 0; i < 32; i++)
                            _clientPub[i] = data[r + i];
                        hasPub = true;
                    }
                    r += klen;
                }
            }
            else if (extType == 13)         // signature_algorithms
            {
                int listLen = (data[q] << 8) | data[q + 1];
                for (int i = 0; i + 1 < listLen; i += 2)
                {
                    int alg = (data[q + 2 + i] << 8) | data[q + 3 + i];
                    if (alg == 0x0807)
                        sigOk = true;
                }
            }
            else if (extType == 16)         // ALPN
            {
                int listLen = (data[q] << 8) | data[q + 1];
                int r = q + 2;
                int listEnd = r + listLen;
                while (r < listEnd)
                {
                    int nl = data[r++];
                    if (r + nl > listEnd)
                        break;
                    if (nl == 2 && data[r] == (byte)'h' && data[r + 1] == (byte)'3')
                        sawH3 = true;
                    r += nl;
                }
            }
            p = q + extLen;
        }

        if (!has1301 || !versionOk || !hasPub || !sigOk || !sawH3)
            return 4;
        AlpnSelected = "h3";
        return 0;
    }

    /// <summary>
    /// Handle the ClientHello CRYPTO bytes and build the server flight.
    /// Returns false when the hello is unacceptable.
    /// </summary>
    public bool ProcessClientHello(byte[] data, int off, int len, byte[] serverTp, int serverTpLen)
    {
        if (ParseClientHello(data, off, len) != 0)
            return false;
        int bodyLen = (data[off + 1] << 16) | (data[off + 2] << 8) | data[off + 3];
        AppendT(data, off, 4 + bodyLen);

        // ECDHE with X25519.
        byte[] seed = Csprng.GetBytes(32);
        byte[] serverPub = X25519.ScalarMultBase(seed);
        byte[] shared = X25519.ScalarMult(seed, _clientPub);

        // ServerHello -> OutInitial (and to the transcript!).
        OutInitialLen = BuildServerHello(serverPub);
        AppendT(OutInitial, 0, OutInitialLen);

        // Key schedule up to handshake secrets (RFC 8446 7.1).
        byte[] handshakeSecret = DeriveHandshakeSecret(shared);
        byte[] hsCtx = HashT();
        _clientHsTraffic = DeriveLabelWithHash(handshakeSecret, "c hs traffic", hsCtx);
        _serverHsTraffic = DeriveLabelWithHash(handshakeSecret, "s hs traffic", hsCtx);
        byte[] masterSecret = DeriveMasterSecret(handshakeSecret);
        DerivePacketKeys(_serverHsTraffic, ServerHsKey, ServerHsIv, ServerHsHp);
        DerivePacketKeys(_clientHsTraffic, ClientHsKey, ClientHsIv, ClientHsHp);

        // Server flight: EE, Certificate, CertificateVerify, Finished.
        var ee = new byte[1024];
        int en = BuildEeMessage(serverTp, serverTpLen, ee);
        AppendT(ee, 0, en);

        var cert = new byte[1024];
        int cn = BuildCertMessage(cert);
        AppendT(cert, 0, cn);

        var cv = new byte[128];
        int vn = BuildCvMessage(cv);
        AppendT(cv, 0, vn);

        var fin = new byte[64];
        int fn = BuildFinishedMessage(fin);
        AppendT(fin, 0, fn);

        AssembleFlight(ee, en, cert, cn, cv, vn, fin, fn);

        // Application secrets over the transcript including server Finished.
        byte[] th2 = HashT();
        _clientApTraffic = DeriveLabelWithHash(masterSecret, "c ap traffic", th2);
        _serverApTraffic = DeriveLabelWithHash(masterSecret, "s ap traffic", th2);
        DerivePacketKeys(_serverApTraffic, ServerAppKey, ServerAppIv, ServerAppHp);
        DerivePacketKeys(_clientApTraffic, ClientAppKey, ClientAppIv, ClientAppHp);
        return true;
    }

    private byte[] DeriveHandshakeSecret(byte[] shared)
    {
        byte[] zeros = new byte[32];
        byte[] emptyHash = Sha256.Hash(new byte[0]);
        byte[] earlySecret = Hkdf.Extract(HashKind.Sha256, null, zeros, 0, 32);
        byte[] derived = DeriveLabelWithHash(earlySecret, "derived", emptyHash);
        return Hkdf.Extract(HashKind.Sha256, derived, shared, 0, 32);
    }

    private byte[] DeriveMasterSecret(byte[] handshakeSecret)
    {
        byte[] zeros = new byte[32];
        byte[] emptyHash = Sha256.Hash(new byte[0]);
        byte[] derived2 = DeriveLabelWithHash(handshakeSecret, "derived", emptyHash);
        return Hkdf.Extract(HashKind.Sha256, derived2, zeros, 0, 32);
    }

    private int BuildServerHello(byte[] serverPub)
    {
        var sh = OutInitial;
        int sn = 0;
        sh[sn++] = 2;                            // handshake type
        sh[sn++] = 0;
        sh[sn++] = 0;
        sh[sn++] = 0;
        sh[sn++] = 0x03;                         // legacy_version
        sh[sn++] = 0x03;
        byte[] srand = Csprng.GetBytes(32);
        for (int i = 0; i < 32; i++)
            sh[sn++] = srand[i];
        sh[sn++] = (byte)_sessionIdLen;
        for (int i = 0; i < _sessionIdLen; i++)
            sh[sn++] = _sessionId[i];
        sh[sn++] = 0x13;                         // AES-128-GCM-SHA256
        sh[sn++] = 0x01;
        sh[sn++] = 0;
        sh[sn++] = 0;                            // extensions block length
        sh[sn++] = 46;
        sh[sn++] = 0;                            // supported_versions
        sh[sn++] = 43;
        sh[sn++] = 0;
        sh[sn++] = 2;
        sh[sn++] = 0x03;
        sh[sn++] = 0x04;
        sh[sn++] = 0;                            // key_share
        sh[sn++] = 51;
        sh[sn++] = 0;
        sh[sn++] = 36;
        sh[sn++] = 0;
        sh[sn++] = 0x1D;                         // x25519
        sh[sn++] = 0;
        sh[sn++] = 32;
        for (int i = 0; i < 32; i++)
            sh[sn++] = serverPub[i];
        int shBody = sn - 4;
        sh[1] = (byte)(shBody >> 16);
        sh[2] = (byte)(shBody >> 8);
        sh[3] = (byte)shBody;
        return sn;
    }

    private int BuildEeMessage(byte[] serverTp, int tpLen, byte[] ee)
    {
        int en = 0;
        ee[en++] = 8;
        ee[en++] = 0;
        ee[en++] = 0;
        ee[en++] = 0;
        int extBytesLen = 9 + 4 + tpLen;         // ALPN ext + TP ext
        ee[en++] = (byte)(extBytesLen >> 8);
        ee[en++] = (byte)extBytesLen;
        // ALPN: ext(type 0x0010, len 5) = ProtocolNameList(3) = h3
        ee[en++] = 0;
        ee[en++] = 16;
        ee[en++] = 0;
        ee[en++] = 5;
        ee[en++] = 0;
        ee[en++] = 3;
        ee[en++] = 2;
        ee[en++] = (byte)'h';
        ee[en++] = (byte)'3';
        // quic_transport_parameters
        ee[en++] = 0;
        ee[en++] = 0x39;
        ee[en++] = (byte)(tpLen >> 8);
        ee[en++] = (byte)tpLen;
        for (int i = 0; i < tpLen; i++)
            ee[en++] = serverTp[i];
        int eeBody = en - 4;
        ee[1] = (byte)(eeBody >> 16);
        ee[2] = (byte)(eeBody >> 8);
        ee[3] = (byte)eeBody;
        return en;
    }

    private int BuildCertMessage(byte[] cert)
    {
        int certLen = _certDer.Length;
        int cn = 0;
        cert[cn++] = 11;
        int certBody = 1 + 3 + 3 + certLen + 2;
        cert[cn++] = (byte)(certBody >> 16);
        cert[cn++] = (byte)(certBody >> 8);
        cert[cn++] = (byte)certBody;
        cert[cn++] = 0;                          // request context
        int entryLen = 3 + certLen + 2;
        cert[cn++] = (byte)(entryLen >> 16);
        cert[cn++] = (byte)(entryLen >> 8);
        cert[cn++] = (byte)entryLen;
        cert[cn++] = (byte)(certLen >> 16);
        cert[cn++] = (byte)(certLen >> 8);
        cert[cn++] = (byte)certLen;
        for (int i = 0; i < certLen; i++)
            cert[cn++] = _certDer[i];
        cert[cn++] = 0;                          // extensions
        cert[cn++] = 0;
        return cn;
    }

    private int BuildCvMessage(byte[] cv)
    {
        byte[] th = HashT();
        byte[] content = BuildCertVerifyContent(th);
        byte[] signature = Ed25519.Sign(_keySeed, content);
        int vn = 0;
        cv[vn++] = 15;
        int cvBody = 2 + 2 + signature.Length;
        cv[vn++] = (byte)(cvBody >> 16);
        cv[vn++] = (byte)(cvBody >> 8);
        cv[vn++] = (byte)cvBody;
        cv[vn++] = 0x08;
        cv[vn++] = 0x07;
        cv[vn++] = (byte)(signature.Length >> 8);
        cv[vn++] = (byte)signature.Length;
        for (int i = 0; i < signature.Length; i++)
            cv[vn++] = signature[i];
        return vn;
    }

    private int BuildFinishedMessage(byte[] fin)
    {
        byte[] finishedKey = QuicExpand(_serverHsTraffic, "finished", 32);
        byte[] th = HashT();
        byte[] verifyData = Hmac.Compute(HashKind.Sha256, finishedKey, th);
        fin[0] = 20;
        fin[1] = 0;
        fin[2] = 0;
        fin[3] = 32;
        for (int i = 0; i < 32; i++)
            fin[4 + i] = verifyData[i];
        return 36;
    }

    private void AssembleFlight(byte[] ee, int en, byte[] cert, int cn,
        byte[] cv, int vn, byte[] fin, int fn)
    {
        int hn = 0;
        for (int i = 0; i < en; i++)
            OutHandshake[hn++] = ee[i];
        for (int i = 0; i < cn; i++)
            OutHandshake[hn++] = cert[i];
        for (int i = 0; i < vn; i++)
            OutHandshake[hn++] = cv[i];
        for (int i = 0; i < fn; i++)
            OutHandshake[hn++] = fin[i];
        OutHandshakeLen = hn;
    }

    /// <summary>Verify the client Finished CRYPTO bytes; completes the handshake.</summary>
    public bool ProcessClientFinished(byte[] data, int off, int len, bool complete)
    {
        _ = complete;
        if (HandshakeComplete)
            return true;
        if (len < 4 + 32)
            return false;
        if (data[off] != 20)
            return false;
        byte[] finishedKey = QuicExpand(_clientHsTraffic, "finished", 32);
        byte[] expected = Hmac.Compute(HashKind.Sha256, finishedKey, HashT());
        for (int i = 0; i < 32; i++)
        {
            if (data[off + 4 + i] != expected[i])
                return false;
        }
        AppendT(data, off, 4 + 32);
        HandshakeComplete = true;
        return true;
    }

    // ==================== helpers ====================

    private static byte[] DeriveLabelWithHash(byte[] secret, string label, byte[] context)
    {
        // Expand-Label with a context.
        int ctxLen = context == null ? 0 : context.Length;
        var info = new byte[2 + 1 + 6 + label.Length + 1 + ctxLen];
        int pos = 0;
        info[pos++] = 0;
        info[pos++] = 32;
        info[pos++] = (byte)(6 + label.Length);
        byte[] prefix = new byte[] { (byte)'t', (byte)'l', (byte)'s', (byte)'1', (byte)'3', (byte)' ' };
        for (int i = 0; i < 6; i++)
            info[pos++] = prefix[i];
        for (int i = 0; i < label.Length; i++)
            info[pos++] = (byte)label[i];
        info[pos++] = (byte)ctxLen;
        for (int i = 0; i < ctxLen; i++)
            info[pos++] = context[i];
        return Hkdf.Expand(HashKind.Sha256, secret, info, 32);
    }

    private static byte[] BuildCertVerifyContent(byte[] transcriptHash)
    {
        byte[] label = new byte[]
        {
            (byte)'T', (byte)'L', (byte)'S', (byte)' ', (byte)'1', (byte)'.',
            (byte)'3', (byte)',', (byte)' ', (byte)'s', (byte)'e', (byte)'r',
            (byte)'v', (byte)'e', (byte)'r', (byte)' ', (byte)'C', (byte)'e',
            (byte)'r', (byte)'t', (byte)'i', (byte)'f', (byte)'i', (byte)'c',
            (byte)'a', (byte)'t', (byte)'e', (byte)'V', (byte)'e', (byte)'r',
            (byte)'i', (byte)'f', (byte)'y',
        };
        var content = new byte[64 + label.Length + 1 + transcriptHash.Length];
        for (int i = 0; i < 64; i++)
            content[i] = 0x20;
        int pos = 64;
        for (int i = 0; i < label.Length; i++)
            content[pos++] = label[i];
        content[pos++] = 0;
        for (int i = 0; i < transcriptHash.Length; i++)
            content[pos++] = transcriptHash[i];
        return content;
    }
}
