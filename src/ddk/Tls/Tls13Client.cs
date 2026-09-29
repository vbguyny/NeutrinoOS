// ProtonOS DDK - TLS 1.3 client (Phase 5 HTTPS utilities)
//
// Minimal RFC 8446 client for the NeutrinoOS HTTPS capability, mirroring
// the server in Tls13.cs: X25519 key exchange, AEAD suites
// TLS_AES_128_GCM_SHA256 (0x1301) and TLS_AES_256_GCM_SHA384 (0x1302),
// SNI, and a blocking handshake driver on top of a connected TcpSocket.
//
// Scope / documented limitations:
//   - NO certificate verification: the chain, its signature and the
//     hostname are not validated (no RSA/ECDSA/Ed25519 verify path is
//     wired in here). The handshake itself is still authenticated by the
//     key schedule: the server Finished MAC is verified against secrets
//     derived from the X25519 exchange, so a passive or mismatched peer
//     cannot complete the handshake.
//   - no HelloRetryRequest (x25519 is offered first; a server that wants
//     a different group gets a clear error), no session resumption
//     (NewSessionTicket messages are ignored), no KeyUpdate (a peer
//     request fails the connection), no client certificates.
//
// Everything is synchronous: Handshake()/ReadApp() pump the NIC through
// NetworkPump while they wait, so callers (wget/curl) keep the plain
// send/receive loop shape of the http:// path.

using System;
using ProtonOS.DDK.Crypto;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;
using ProtonOS.DDK.Network.Stack;

namespace ProtonOS.DDK.Tls;

/// <summary>TLS 1.3 client connection state (see file header).</summary>
public sealed unsafe class Tls13Client
{
    // Record content types.
    private const byte CtChangeCipherSpec = 20;
    private const byte CtAlert = 21;
    private const byte CtHandshake = 22;
    private const byte CtApplicationData = 23;

    // Handshake message types.
    private const byte HtServerHello = 2;
    private const byte HtNewSessionTicket = 4;
    private const byte HtEncryptedExtensions = 8;
    private const byte HtCertificate = 11;
    private const byte HtCertificateRequest = 13;
    private const byte HtCertificateVerify = 15;
    private const byte HtFinished = 20;
    private const byte HtKeyUpdate = 24;

    private const int StateHandshake = 0;
    private const int StateConnected = 1;
    private const int StateClosed = 2;
    private const int StateError = 3;

    private const int RecPlaintext = 0;
    private const int RecHandshake = 1;
    private const int RecApplication = 2;

    private readonly TcpSocket _sock;
    private readonly NetworkStack _stack;
    private readonly string _serverName;

    private int _state = StateHandshake;
    private string _lastError;

    // Negotiated parameters.
    private HashKind _hashKind = HashKind.Sha256;
    private int _hashLen = 32;
    private int _keyLen = 16;
    private ushort _suite = 0x1301;

    // Key schedule products.
    private byte[] _clientHsTraffic;
    private byte[] _serverHsTraffic;
    private byte[] _clientApTraffic;
    private byte[] _serverApTraffic;
    private byte[] _masterSecret;
    private byte[] _seed;               // X25519 private scalar

    // Protection state (per direction).
    private int _rxProtection = RecPlaintext;
    private int _txProtection = RecPlaintext;
    private byte[] _rxKey;
    private byte[] _rxIv;
    private byte[] _txKey;
    private byte[] _txIv;
    private ulong _rxSeq;
    private ulong _txSeq;

    // Buffers.
    private readonly byte[] _rx = new byte[18432];
    private int _rxLen;
    private readonly byte[] _hs = new byte[16384];
    private int _hsLen;
    private byte[] _transcript = new byte[2048];
    private int _transcriptLen;
    private readonly byte[] _appPending = new byte[16384];
    private int _appPendingLen;

    /// <summary>Write handshake progress to the console (debug only).</summary>
    public static bool Verbose;

    /// <summary>Create a client over a connected socket.</summary>
    public Tls13Client(TcpSocket sock, NetworkStack stack, string serverName)
    {
        _sock = sock;
        _stack = stack;
        _serverName = serverName;
    }

    /// <summary>True once the handshake completed.</summary>
    public bool Connected => _state == StateConnected;

    /// <summary>True when the connection is closed (peer close or error).</summary>
    public bool Closed => _state == StateClosed || _state == StateError;

    /// <summary>Human-readable reason for the last failure (null when fine).</summary>
    public string LastError => _lastError;

    /// <summary>Negotiated cipher suite id (0x1301 / 0x1302).</summary>
    public ushort SuiteId => _suite;

    // ==================== Public driving API ====================

    /// <summary>
    /// Runs the full client handshake (ClientHello .. own Finished),
    /// pumping the NIC while waiting. Returns true when application data
    /// may flow.
    /// </summary>
    public bool Handshake(int timeoutMs)
    {
        if (_state == StateConnected)
            return true;
        if (_state != StateHandshake)
            return false;

        _seed = Csprng.GetBytes(32);
        byte[] pub = X25519.ScalarMultBase(_seed);
        byte[] clientHello = BuildClientHello(pub);

        // First record may carry the legacy 0x0301 version for middlebox
        // compatibility (RFC 8446 section 5.1).
        var msg = new byte[4 + clientHello.Length];
        msg[0] = 1;   // ClientHello
        msg[1] = (byte)(clientHello.Length >> 16);
        msg[2] = (byte)(clientHello.Length >> 8);
        msg[3] = (byte)clientHello.Length;
        for (int i = 0; i < clientHello.Length; i++)
            msg[4 + i] = clientHello[i];
        AppendTranscript(msg, 0, msg.Length);
        SendRecord(0x0301, CtHandshake, msg, 0, msg.Length);
        if (Verbose)
            Console.WriteLine("[tls] clienthello sent (" + msg.Length + " bytes)");

        ulong start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < (ulong)timeoutMs)
        {
            ProcessRecords();
            if (_state == StateConnected)
                return true;
            if (_state != StateHandshake)
                return false;
            // Some servers reset the connection instead of sending an
            // alert (e.g. TLS 1.2-only servers given a 1.3-only hello) -
            // fail fast so callers can fall back to TLS 1.2 promptly.
            if (!_sock.Connected && _sock.Available == 0 && _rxLen == 0)
            {
                Fail("connection reset by peer");
                return false;
            }
        }
        Fail("handshake timeout");
        return false;
    }

    /// <summary>
    /// Reads available application data. Returns the number of plaintext
    /// bytes copied, 0 when nothing is available yet, or -1 when the
    /// connection ended (peer close, alert or error).
    /// </summary>
    public int ReadApp(byte[] destination, int offset, int maxLength)
    {
        if (_state == StateClosed || _state == StateError)
            return -1;
        if (_state != StateConnected)
        {
            if (_appPendingLen == 0)
                ProcessRecords();
            if (_state != StateConnected && _appPendingLen == 0)
                return _state <= StateHandshake ? 0 : -1;
        }
        if (_appPendingLen == 0)
            ProcessRecords();
        if (_state == StateError)
            return -1;
        // Server closed the TCP side without close_notify after the
        // response - treat "peer gone with nothing buffered" as EOF.
        if (_state == StateConnected && !_sock.Connected && _sock.Available == 0
            && _rxLen < 5)
        {
            _state = StateClosed;
        }
        if (_state == StateClosed && _appPendingLen == 0)
            return -1;
        return DrainPending(destination, offset, maxLength);
    }

    /// <summary>Sends application data as one or more TLS records.</summary>
    public void WriteApp(byte[] data, int offset, int length)
    {
        if (_state != StateConnected)
            return;
        int pos = 0;
        while (pos < length)
        {
            int chunk = length - pos;
            if (chunk > 16000)
                chunk = 16000;
            SendRecord(0x0303, CtApplicationData, data, offset + pos, chunk);
            pos += chunk;
        }
    }

    /// <summary>Sends close_notify (best effort) and closes.</summary>
    public void CloseGraceful()
    {
        if (_state == StateConnected)
        {
            var alert = new byte[] { 1, 0 };   // warning, close_notify
            SendRecord(0x0303, CtAlert, alert, 0, alert.Length);
        }
        Close();
    }

    /// <summary>Closes the underlying socket.</summary>
    public void Close()
    {
        if (_state == StateClosed || _state == StateError)
            return;
        _state = StateClosed;
        _sock.Close();
    }

    // ==================== ClientHello construction ====================

    private byte[] BuildClientHello(byte[] pub)
    {
        byte[] random = Csprng.GetBytes(32);
        byte[] sid = Csprng.GetBytes(32);
        byte[] host = Ascii(_serverName);
        int hostLen = host.Length > 255 ? 255 : host.Length;

        var exts = new TlsWriter(320);
        // server_name (SNI): list len = 1 + 2 + hostLen.
        exts.U16(0);
        exts.U16(2 + 1 + 2 + hostLen);
        exts.U16(1 + 2 + hostLen);
        exts.U8(0);
        exts.U16(hostLen);
        exts.Bytes(host, 0, hostLen);
        // supported_groups: NamedGroupList { x25519 }.
        exts.U16(0x000a);
        exts.U16(4);
        exts.U16(2);
        exts.U16(0x001d);
        // signature_algorithms: list length 14, then the seven schemes.
        exts.U16(0x000d);
        exts.U16(16);
        exts.U16(14);
        exts.U16(0x0403);   // ecdsa_secp256r1_sha256
        exts.U16(0x0503);   // ecdsa_secp384r1_sha384
        exts.U16(0x0804);   // rsa_pss_rsae_sha256
        exts.U16(0x0805);   // rsa_pss_rsae_sha384
        exts.U16(0x0401);   // rsa_pkcs1_sha256
        exts.U16(0x0501);   // rsa_pkcs1_sha384
        exts.U16(0x0807);   // ed25519
        // supported_versions: ProtocolVersionList { TLS 1.3 }.
        exts.U16(0x002b);
        exts.U16(3);
        exts.U8(2);
        exts.U16(0x0304);
        // key_share: client_shares { x25519 } - the vector length is
        // part of the extension data (38 = 2 + 4 + 32).
        exts.U16(0x0033);
        exts.U16(38);
        exts.U16(36);
        exts.U16(0x001d);
        exts.U16(32);
        exts.Bytes(pub, 0, 32);

        var body = new TlsWriter(640);
        body.U16(0x0303);
        body.Bytes(random, 0, 32);
        body.U8(32);
        body.Bytes(sid, 0, 32);
        body.U16(4);
        body.U16(0x1301);
        body.U16(0x1302);
        body.U8(1);
        body.U8(0);
        body.U16(exts.Length);
        body.Bytes(exts.Buffer, 0, exts.Length);
        return Slice(body);
    }

    // ==================== Record layer ====================

    private void ProcessRecords()
    {
        ReceiveMore();

        while (_state != StateClosed && _state != StateError)
        {
            if (_rxLen < 5)
                break;
            byte type = _rx[0];
            int recLen = (_rx[3] << 8) | _rx[4];
            if (recLen > 16640)
            {
                Fail("oversized record");
                break;
            }

            if (type == CtChangeCipherSpec && recLen <= 1)
            {
                // Middlebox-compat CCS: plaintext, ignored.
                if (_rxLen < 5 + recLen)
                    break;
                Consume(5 + recLen);
                continue;
            }

            if (_rxProtection == RecPlaintext)
            {
                if (_rxLen < 5 + recLen)
                    break;
                if (type == CtHandshake)
                {
                    AppendHandshake(_rx, 5, recLen);
                    Consume(5 + recLen);
                    HandleHandshakeMessages();
                }
                else
                {
                    Fail("unexpected plaintext record type " + type);
                }
                continue;
            }

            // Encrypted record: 5-byte header + ciphertext + 16-byte tag;
            // the record length field INCLUDES the tag (RFC 8446 5.2).
            if (recLen < 17)
            {
                Fail("short encrypted record");
                break;
            }
            if (_rxLen < 5 + recLen)
                break;

            int ctLen = recLen - 16;
            var header = new byte[5];
            for (int i = 0; i < 5; i++)
                header[i] = _rx[i];
            var tag = new byte[16];
            for (int i = 0; i < 16; i++)
                tag[i] = _rx[5 + ctLen + i];
            byte[] nonce = BuildNonce(_rxIv, _rxSeq);
            var aead = new AesGcm(_rxKey);
            if (!aead.Open(nonce, header, _rx, 5, ctLen, tag, out byte[] plain))
            {
                Fail("record decrypt failed (seq " + _rxSeq + ")");
                return;
            }
            _rxSeq++;
            Consume(5 + recLen);

            // Inner plaintext = content || content_type || zeros.
            int contentEnd = ctLen - 1;
            while (contentEnd >= 0 && plain[contentEnd] == 0)
                contentEnd--;
            if (contentEnd < 0)
                continue;
            byte innerType = plain[contentEnd];

            if (innerType == CtApplicationData)
            {
                StageApp(plain, 0, contentEnd);
            }
            else if (innerType == CtHandshake)
            {
                AppendHandshake(plain, 0, contentEnd);
                HandleHandshakeMessages();
            }
            else if (innerType == CtAlert)
            {
                if (contentEnd >= 1 && plain[1] == 0)
                {
                    // close_notify: graceful peer close.
                    _state = StateClosed;
                }
                else
                {
                    Fail("peer alert " + (contentEnd >= 1 ? plain[1] : 0));
                }
            }
            else
            {
                Fail("unexpected inner content type " + innerType);
            }
        }
    }

    private void SendRecord(ushort legacyVersion, byte type, byte[] data, int offset, int length)
    {
        byte[] wire;
        if (_txProtection == RecPlaintext)
        {
            wire = new byte[5 + length];
            wire[0] = type;
            wire[1] = (byte)(legacyVersion >> 8);
            wire[2] = (byte)legacyVersion;
            wire[3] = (byte)(length >> 8);
            wire[4] = (byte)length;
            for (int i = 0; i < length; i++)
                wire[5 + i] = data[offset + i];
        }
        else
        {
            int innerLen = length + 1;
            var inner = new byte[innerLen];
            for (int i = 0; i < length; i++)
                inner[i] = data[offset + i];
            inner[length] = type;
            var header = new byte[5];
            header[0] = CtApplicationData;
            header[1] = 0x03;
            header[2] = 0x03;
            header[3] = (byte)((innerLen + 16) >> 8);
            header[4] = (byte)(innerLen + 16);
            byte[] nonce = BuildNonce(_txIv, _txSeq);
            var aead = new AesGcm(_txKey);
            byte[] sealed_ = aead.Seal(nonce, header, inner);
            _txSeq++;
            wire = new byte[5 + sealed_.Length];
            for (int i = 0; i < 5; i++)
                wire[i] = header[i];
            for (int i = 0; i < sealed_.Length; i++)
                wire[5 + i] = sealed_[i];
        }
        SendAll(wire);
    }

    private void SendAll(byte[] data)
    {
        int sent = 0;
        int guard = 0;
        while (sent < data.Length && guard < 128)
        {
            guard++;
            int n;
            fixed (byte* p = data)
            {
                n = _sock.Send(p + sent, data.Length - sent);
            }
            if (n <= 0)
                return;
            sent += n;
        }
        // The stack queues segments; the caller transmits them.
        NetworkPump.FlushTx(_stack);
    }

    private void ReceiveMore()
    {
        NetworkPump.Pump(_stack, 8);
        while (_rxLen < _rx.Length)
        {
            int n;
            fixed (byte* p = _rx)
            {
                n = _sock.Receive(p + _rxLen, _rx.Length - _rxLen);
            }
            if (n <= 0)
                return;
            _rxLen += n;
            if (n < 1024)
                return;
        }
    }

    private void Consume(int count)
    {
        for (int i = count; i < _rxLen; i++)
            _rx[i - count] = _rx[i];
        _rxLen -= count;
    }

    private void AppendHandshake(byte[] data, int offset, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (_hsLen >= _hs.Length)
                return;
            _hs[_hsLen++] = data[offset + i];
        }
    }

    private void ConsumeHandshake(int count)
    {
        for (int i = count; i < _hsLen; i++)
            _hs[i - count] = _hs[i];
        _hsLen -= count;
    }

    private static byte[] BuildNonce(byte[] iv, ulong seq)
    {
        var nonce = new byte[12];
        for (int i = 0; i < 12; i++)
            nonce[i] = iv[i];
        for (int i = 0; i < 8; i++)
            nonce[11 - i] ^= (byte)(seq >> (8 * i));
        return nonce;
    }

    private void StageApp(byte[] data, int offset, int length)
    {
        for (int i = 0; i < length && _appPendingLen < _appPending.Length; i++)
            _appPending[_appPendingLen++] = data[offset + i];
    }

    private int DrainPending(byte[] destination, int offset, int maxLength)
    {
        int n = _appPendingLen < maxLength ? _appPendingLen : maxLength;
        for (int i = 0; i < n; i++)
            destination[offset + i] = _appPending[i];
        for (int i = n; i < _appPendingLen; i++)
            _appPending[i - n] = _appPending[i];
        _appPendingLen -= n;
        return n;
    }

    // ==================== Handshake message dispatch ====================

    private void HandleHandshakeMessages()
    {
        while (_hsLen >= 4 && _state == StateHandshake)
        {
            int bodyLen = (_hs[1] << 16) | (_hs[2] << 8) | _hs[3];
            if (_hsLen < 4 + bodyLen)
                break;

            byte type = _hs[0];
            var msg = new byte[4 + bodyLen];
            for (int i = 0; i < 4 + bodyLen; i++)
                msg[i] = _hs[i];
            ConsumeHandshake(4 + bodyLen);
            HandleMessage(type, msg, bodyLen);
        }
    }

    private void HandleMessage(byte type, byte[] msg, int bodyLen)
    {
        if (type == HtServerHello)
        {
            HandleServerHello(msg, bodyLen);
            return;
        }

        // All other server messages are already encrypted. The Finished
        // MAC is verified BEFORE it enters the transcript (RFC 8446);
        // the rest are appended and otherwise ignored (no certificate
        // validation - see the file header).
        if (type == HtFinished)
        {
            byte[] finishedKey = ExpandLabel(_serverHsTraffic, "finished", null, _hashLen);
            byte[] expected = Hmac.Compute(_hashKind, finishedKey, HashTranscript());
            if (!FixedEqual(expected, msg, 4, bodyLen))
            {
                Fail("server Finished verify failed");
                return;
            }
            AppendTranscript(msg, 0, 4 + bodyLen);
            OnServerFinished();
            return;
        }

        if (type == HtEncryptedExtensions || type == HtCertificate
            || type == HtCertificateVerify || type == HtCertificateRequest)
        {
            AppendTranscript(msg, 0, 4 + bodyLen);
            return;
        }

        if (type == HtNewSessionTicket)
        {
            // Session resumption is not implemented; ignore.
            AppendTranscript(msg, 0, 4 + bodyLen);
            return;
        }

        if (type == HtKeyUpdate)
        {
            Fail("peer requested KeyUpdate (not supported)");
            return;
        }

        Fail("unexpected handshake type " + type);
    }

    private void HandleServerHello(byte[] msg, int bodyLen)
    {
        AppendTranscript(msg, 0, 4 + bodyLen);

        var r = new TlsReader(msg, 4, bodyLen);
        r.U16();                        // legacy_version
        byte[] random = r.Bytes(32);
        int sidLen = r.U8();
        r.Skip(sidLen);                 // legacy_session_id echo
        int suite = r.U16();
        r.U8();                         // compression
        int extTotal = r.U16();
        int extEnd = r.Position + extTotal;

        byte[] serverPub = null;
        bool versionOk = false;
        while (r.Position + 4 <= extEnd)
        {
            int extType = r.U16();
            int extLen = r.U16();
            int extStart = r.Position;
            if (extType == 0x002b)
            {
                if (r.U16() == 0x0304)
                    versionOk = true;
            }
            else if (extType == 0x0033)
            {
                int group = r.U16();
                int klen = r.U16();
                if (group == 0x001d && klen == 32)
                    serverPub = r.Bytes(32);
            }
            r.Position = extStart + extLen;
        }

        if (IsHelloRetryRequest(random))
        {
            Fail("server sent HelloRetryRequest (unsupported)");
            return;
        }
        if (!versionOk)
        {
            Fail("server did not select TLS 1.3");
            return;
        }
        if (serverPub == null)
        {
            Fail("server key_share missing (wanted x25519)");
            return;
        }
        if (suite == 0x1301)
        {
            _suite = 0x1301;
            _hashKind = HashKind.Sha256;
            _hashLen = 32;
            _keyLen = 16;
        }
        else if (suite == 0x1302)
        {
            _suite = 0x1302;
            _hashKind = HashKind.Sha384;
            _hashLen = 48;
            _keyLen = 32;
        }
        else
        {
            Fail("server selected unsupported suite 0x" + suite);
            return;
        }

        // ECDHE + key schedule (RFC 8446 section 7.1). The "derived"
        // secrets hash the EMPTY transcript, not the current one.
        byte[] shared = X25519.ScalarMult(_seed, serverPub);
        byte[] zeros = new byte[_hashLen];
        byte[] emptyHash = HashBytes(new byte[0]);
        byte[] earlySecret = Hkdf.Extract(_hashKind, null, zeros, 0, zeros.Length);
        byte[] derived = DeriveSecret(earlySecret, "derived", emptyHash);
        byte[] handshakeSecret = Hkdf.Extract(_hashKind, derived, shared, 0, 32);
        _clientHsTraffic = DeriveSecret(handshakeSecret, "c hs traffic", HashTranscript());
        _serverHsTraffic = DeriveSecret(handshakeSecret, "s hs traffic", HashTranscript());
        byte[] derived2 = DeriveSecret(handshakeSecret, "derived", emptyHash);
        _masterSecret = Hkdf.Extract(_hashKind, derived2, zeros, 0, zeros.Length);

        // RX/TX switch to handshake protection.
        _rxKey = ExpandLabel(_serverHsTraffic, "key", null, _keyLen);
        _rxIv = ExpandLabel(_serverHsTraffic, "iv", null, 12);
        _rxSeq = 0;
        _rxProtection = RecHandshake;
        _txKey = ExpandLabel(_clientHsTraffic, "key", null, _keyLen);
        _txIv = ExpandLabel(_clientHsTraffic, "iv", null, 12);
        _txSeq = 0;
        _txProtection = RecHandshake;
        if (Verbose)
            Console.WriteLine("[tls] serverhello ok suite=0x" + _suite);
    }

    private void OnServerFinished()
    {
        // Application secrets (transcript through the server Finished).
        _clientApTraffic = DeriveSecret(_masterSecret, "c ap traffic", HashTranscript());
        _serverApTraffic = DeriveSecret(_masterSecret, "s ap traffic", HashTranscript());

        // Client Finished (still under handshake keys).
        byte[] finishedKey = ExpandLabel(_clientHsTraffic, "finished", null, _hashLen);
        byte[] verifyData = Hmac.Compute(_hashKind, finishedKey, HashTranscript());
        var msg = new byte[4 + verifyData.Length];
        msg[0] = HtFinished;
        msg[1] = (byte)(verifyData.Length >> 16);
        msg[2] = (byte)(verifyData.Length >> 8);
        msg[3] = (byte)verifyData.Length;
        for (int i = 0; i < verifyData.Length; i++)
            msg[4 + i] = verifyData[i];
        AppendTranscript(msg, 0, msg.Length);
        SendRecord(0x0303, CtHandshake, msg, 0, msg.Length);
        if (Verbose)
            Console.WriteLine("[tls] client finished sent");

        // Switch both directions to application protection.
        _txKey = ExpandLabel(_clientApTraffic, "key", null, _keyLen);
        _txIv = ExpandLabel(_clientApTraffic, "iv", null, 12);
        _txSeq = 0;
        _txProtection = RecApplication;
        _rxKey = ExpandLabel(_serverApTraffic, "key", null, _keyLen);
        _rxIv = ExpandLabel(_serverApTraffic, "iv", null, 12);
        _rxSeq = 0;
        _rxProtection = RecApplication;
        _state = StateConnected;
    }

    private void Fail(string reason)
    {
        if (_state == StateClosed || _state == StateError)
            return;
        if (_lastError == null)
            _lastError = reason;
        // Best-effort handshake_failure alert before giving up.
        if (_state == StateHandshake && _txProtection != RecPlaintext)
        {
            var alert = new byte[] { 2, 40 };
            SendRecord(0x0303, CtAlert, alert, 0, alert.Length);
        }
        _state = StateError;
        _sock.Close();
    }

    // ==================== Helpers ====================

    private void AppendTranscript(byte[] data, int offset, int length)
    {
        if (_transcriptLen + length > _transcript.Length)
        {
            var bigger = new byte[(_transcriptLen + length) * 2];
            for (int i = 0; i < _transcriptLen; i++)
                bigger[i] = _transcript[i];
            _transcript = bigger;
        }
        for (int i = 0; i < length; i++)
            _transcript[_transcriptLen++] = data[offset + i];
    }

    private byte[] HashTranscript()
    {
        var data = new byte[_transcriptLen];
        for (int i = 0; i < _transcriptLen; i++)
            data[i] = _transcript[i];
        return HashBytes(data);
    }

    private byte[] HashBytes(byte[] data)
    {
        if (_hashKind == HashKind.Sha384)
            return Sha384.Hash(data);
        return Sha256.Hash(data);
    }

    private byte[] DeriveSecret(byte[] secret, string label, byte[] transcriptHash)
        => ExpandLabel(secret, label, transcriptHash, _hashLen);

    private byte[] ExpandLabel(byte[] secret, string label, byte[] context, int length)
    {
        byte[] fullLabel = Ascii("tls13 ");
        var info = new TlsWriter(64);
        info.U16(length);
        info.U8((byte)(fullLabel.Length + label.Length));
        info.Bytes(fullLabel, 0, fullLabel.Length);
        byte[] labelBytes = Ascii(label);
        info.Bytes(labelBytes, 0, labelBytes.Length);
        int ctxLen = context == null ? 0 : context.Length;
        info.U8((byte)ctxLen);
        if (ctxLen > 0)
            info.Bytes(context, 0, ctxLen);
        return Hkdf.Expand(_hashKind, secret, Slice(info), length);
    }

    private static bool FixedEqual(byte[] expected, byte[] actual, int offset, int length)
    {
        if (length != expected.Length)
            return false;
        for (int i = 0; i < length; i++)
        {
            if (actual[offset + i] != expected[i])
                return false;
        }
        return true;
    }

    private static bool IsHelloRetryRequest(byte[] random)
    {
        // Special random value from RFC 8446 section 4.1.3.
        byte[] hrr = { 0xCF, 0x21, 0xAD, 0x74, 0xE5, 0x9A, 0x61, 0x11,
                       0xBE, 0x1D, 0x8C, 0x02, 0x1E, 0x65, 0xB8, 0x91,
                       0xC2, 0xA2, 0x11, 0x16, 0x7A, 0xBB, 0x8C, 0x5E,
                       0x07, 0x9E, 0x09, 0xE2, 0xC8, 0xA8, 0x33, 0x9C };
        for (int i = 0; i < 32; i++)
        {
            if (random[i] != hrr[i])
                return false;
        }
        return true;
    }

    private static byte[] Slice(TlsWriter w)
    {
        var result = new byte[w.Length];
        for (int i = 0; i < w.Length; i++)
            result[i] = w.Buffer[i];
        return result;
    }

    private static byte[] Ascii(string s)
    {
        var bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
            bytes[i] = (byte)s[i];
        return bytes;
    }
}
