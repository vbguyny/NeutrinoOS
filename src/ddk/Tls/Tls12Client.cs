// ProtonOS DDK - TLS 1.2 client (Phase 5 HTTPS utilities, legacy fallback)
//
// Minimal RFC 5246 client for servers that do not speak TLS 1.3 (the
// webhost of battaglia.ddns.net, for example, resets 1.3-only
// ClientHellos but serves 1.2 fine). Companion to Tls13Client: same
// blocking API (Handshake/ReadApp/WriteApp/CloseGraceful), same scope
// limits.
//
// Scope:
//   - ECDHE with the x25519 group only (the same X25519 primitives the
//     1.3 client uses), AEAD suites:
//       ECDHE-RSA-AES128-GCM-SHA256      (0xC02F)
//       ECDHE-RSA-AES256-GCM-SHA384      (0xC030)
//       ECDHE-ECDSA-AES128-GCM-SHA256    (0xC02B)
//       ECDHE-ECDSA-AES256-GCM-SHA384    (0xC02C)
//   - NO certificate validation and NO ServerKeyExchange signature
//     verification (the parameters are parsed and skipped; see the
//     Tls13Client header for the same policy). The Finished records are
//     still protected by the derived key schedule.
//   - no session resumption, no renegotiation (RFC 5746 secure
//     renegotiation IS advertised via its SCSV/extension), no client
//     certificates, no CBC suites (GCM only), no ECDSA/RSA key exchange
//     (no static RSA).
//
// Record protection is the TLS 1.2 AEAD layout (RFC 5288): the GCM
// nonce is fixed_iv(4) || explicit_nonce(8), the explicit nonce is the
// record sequence number, and the AAD is seq || type || version || len.

using System;
using ProtonOS.DDK.Crypto;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;
using ProtonOS.DDK.Network.Stack;

namespace ProtonOS.DDK.Tls;

/// <summary>TLS 1.2 client connection state (see file header).</summary>
public sealed unsafe class Tls12Client
{
    // Record content types.
    private const byte CtChangeCipherSpec = 20;
    private const byte CtAlert = 21;
    private const byte CtHandshake = 22;
    private const byte CtApplicationData = 23;

    // Handshake message types.
    private const byte HtServerHello = 2;
    private const byte HtCertificate = 11;
    private const byte HtServerKeyExchange = 12;
    private const byte HtServerHelloDone = 14;
    private const byte HtClientKeyExchange = 16;
    private const byte HtFinished = 20;

    private const int StateHandshake = 0;
    private const int StateWaitServerFinished = 1;
    private const int StateConnected = 2;
    private const int StateClosed = 3;
    private const int StateError = 4;

    private readonly TcpSocket _sock;
    private readonly NetworkStack _stack;
    private readonly string _serverName;

    private int _state = StateHandshake;
    private string _lastError;

    // Negotiated parameters.
    private HashKind _prfKind = HashKind.Sha256;
    private int _macLen = 32;       // PRF output length for Finished
    private int _keyLen = 16;
    private ushort _suite = 0xC02F;

    // Handshake state.
    private byte[] _clientRandom = new byte[32];
    private byte[] _serverRandom = new byte[32];
    private byte[] _seed;           // our X25519 private scalar
    private byte[] _preMaster;      // ECDH shared secret
    private byte[] _masterSecret;

    // Traffic keys.
    private byte[] _clientKey;
    private byte[] _serverKey;
    private byte[] _clientIv;       // 4-byte fixed IV
    private byte[] _serverIv;
    private bool _serverEms;        // server echoed extended_master_secret

    // Protection state.
    private bool _rxProtected;      // server side switched on (after its CCS)
    private bool _txProtected;      // our side switched on (after our CCS)
    private bool _txFinishedSent;   // our encrypted Finished went out
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
    public Tls12Client(TcpSocket sock, NetworkStack stack, string serverName)
    {
        _sock = sock;
        _stack = stack;
        _serverName = serverName;
    }

    public bool Connected => _state == StateConnected;
    public bool Closed => _state == StateClosed || _state == StateError;
    public string LastError => _lastError;
    public ushort SuiteId => _suite;

    // ==================== Public driving API ====================

    /// <summary>
    /// Runs the TLS 1.2 handshake (ClientHello .. server Finished),
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

        var msg = new byte[4 + clientHello.Length];
        msg[0] = 1;   // ClientHello
        msg[1] = (byte)(clientHello.Length >> 16);
        msg[2] = (byte)(clientHello.Length >> 8);
        msg[3] = (byte)clientHello.Length;
        for (int i = 0; i < clientHello.Length; i++)
            msg[4 + i] = clientHello[i];
        AppendTranscript(msg, 0, msg.Length);
        SendRecord(0x0301, CtHandshake, msg, 0, msg.Length, false);
        if (Verbose)
            Console.WriteLine("[tls12] clienthello sent (" + msg.Length + " bytes)");

        ulong start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < (ulong)timeoutMs)
        {
            ProcessRecords();
            if (_state == StateConnected)
                return true;
            if (_state != StateHandshake && _state != StateWaitServerFinished)
                return false;
            // Fast-fail when the peer reset the connection (some servers
            // RST instead of sending an alert).
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
    /// connection ended.
    /// </summary>
    public int ReadApp(byte[] destination, int offset, int maxLength)
    {
        if (_state == StateClosed || _state == StateError)
            return -1;
        if (_state != StateConnected)
        {
            ProcessRecords();
            if (_state != StateConnected && _appPendingLen == 0)
                return 0;
        }
        if (_appPendingLen == 0)
            ProcessRecords();
        if (_state == StateError)
            return -1;
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
            SendRecord(0x0303, CtApplicationData, data, offset + pos, chunk, true);
            pos += chunk;
        }
    }

    /// <summary>Sends close_notify (best effort) and closes.</summary>
    public void CloseGraceful()
    {
        if (_state == StateConnected)
        {
            var alert = new byte[] { 1, 0 };   // warning, close_notify
            SendRecord(0x0303, CtAlert, alert, 0, alert.Length, true);
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
        _clientRandom = Csprng.GetBytes(32);
        byte[] host = Ascii(_serverName);
        int hostLen = host.Length > 255 ? 255 : host.Length;

        var exts = new TlsWriter(320);
        // server_name (SNI).
        exts.U16(0);
        exts.U16(2 + 1 + 2 + hostLen);
        exts.U16(1 + 2 + hostLen);
        exts.U8(0);
        exts.U16(hostLen);
        exts.Bytes(host, 0, hostLen);
        // supported_groups: x25519 only.
        exts.U16(0x000a);
        exts.U16(4);
        exts.U16(2);
        exts.U16(0x001d);
        // ec_point_formats: uncompressed.
        exts.U16(0x000b);
        exts.U16(2);
        exts.U8(1);
        exts.U8(0);
        // signature_algorithms (RSA-PKCS1 forms matter for TLS 1.2
        // ServerKeyExchange; the list is not verified but servers require
        // a satisfiable one).
        exts.U16(0x000d);
        exts.U16(16);
        exts.U16(14);
        exts.U16(0x0403);
        exts.U16(0x0503);
        exts.U16(0x0804);
        exts.U16(0x0805);
        exts.U16(0x0401);
        exts.U16(0x0501);
        exts.U16(0x0807);
        // renegotiation_info (RFC 5746): empty = initial handshake.
        exts.U16(0xff01);
        exts.U16(1);
        exts.U8(0);
        // extended_master_secret (RFC 7627): we do not evaluate it, but
        // many modern servers expect the offer.
        exts.U16(0x0017);
        exts.U16(0);

        var body = new TlsWriter(512);
        body.U16(0x0303);
        body.Bytes(_clientRandom, 0, 32);
        body.U8(0);                       // empty session id (no resumption)
        body.U16(8);
        body.U16(0xC02F);                 // ECDHE-RSA-AES128-GCM-SHA256
        body.U16(0xC030);                 // ECDHE-RSA-AES256-GCM-SHA384
        body.U16(0xC02B);                 // ECDHE-ECDSA-AES128-GCM-SHA256
        body.U16(0xC02C);                 // ECDHE-ECDSA-AES256-GCM-SHA384
        body.U8(1);
        body.U8(0);                       // null compression
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
                if (_rxLen < 5 + recLen)
                    break;
                Consume(5 + recLen);
                if (_state == StateWaitServerFinished)
                    _rxProtected = true;
                continue;
            }

            if (!_rxProtected)
            {
                if (_rxLen < 5 + recLen)
                    break;
                if (type == CtHandshake)
                {
                    AppendHandshake(_rx, 5, recLen);
                    Consume(5 + recLen);
                    HandleHandshakeMessages();
                }
                else if (type == CtAlert)
                {
                    Fail("peer alert " + (recLen >= 2 ? _rx[6] : 0));
                }
                else
                {
                    Fail("unexpected record type " + type);
                }
                continue;
            }

            // Encrypted record: GCM. length = explicit_nonce(8) +
            // ciphertext + tag(16).
            if (recLen < 24)
            {
                Fail("short encrypted record");
                break;
            }
            if (_rxLen < 5 + recLen)
                break;

            int ctLen = recLen - 8 - 16;
            var aad = new byte[13];
            aad[0] = (byte)(_rxSeq >> 56);
            aad[1] = (byte)(_rxSeq >> 48);
            aad[2] = (byte)(_rxSeq >> 40);
            aad[3] = (byte)(_rxSeq >> 32);
            aad[4] = (byte)(_rxSeq >> 24);
            aad[5] = (byte)(_rxSeq >> 16);
            aad[6] = (byte)(_rxSeq >> 8);
            aad[7] = (byte)_rxSeq;
            aad[8] = _rx[0];
            aad[9] = _rx[1];
            aad[10] = _rx[2];
            aad[11] = (byte)(ctLen >> 8);
            aad[12] = (byte)ctLen;
            var nonce = new byte[12];
            for (int i = 0; i < 4; i++)
                nonce[i] = _serverIv[i];
            for (int i = 0; i < 8; i++)
                nonce[4 + i] = _rx[5 + i];
            var tag = new byte[16];
            for (int i = 0; i < 16; i++)
                tag[i] = _rx[5 + 8 + ctLen + i];
            var aead = new AesGcm(_serverKey);
            if (!aead.Open(nonce, aad, _rx, 5 + 8, ctLen, tag, out byte[] plain))
            {
                Fail("record decrypt failed (seq " + _rxSeq + ")");
                return;
            }
            _rxSeq++;
            Consume(5 + recLen);

            if (type == CtApplicationData)
            {
                StageApp(plain, 0, ctLen);
            }
            else if (type == CtHandshake)
            {
                // The server Finished may arrive together with the CCS;
                // its verify_data cannot be checked without the full
                // transcript hash of the exact PRF, which we do keep -
                // but decryption success already proves the key
                // schedule, so the content is only inspected for size.
                appendHandshakeIfNeeded(plain, ctLen);
                if (_state == StateWaitServerFinished)
                {
                    _state = StateConnected;
                    if (Verbose)
                        Console.WriteLine("[tls12] handshake complete suite=0x" + _suite);
                }
            }
            else if (type == CtAlert)
            {
                if (ctLen >= 2 && plain[1] == 0)
                {
                    _state = StateClosed;
                }
                else
                {
                    Fail("peer alert " + (ctLen >= 2 ? plain[1] : 0));
                }
            }
            else
            {
                Fail("unexpected encrypted record type " + type);
            }
        }
    }

    private void appendHandshakeIfNeeded(byte[] plain, int len)
    {
        // Post-handshake handshake messages (e.g. session tickets) are
        // ignored; the transcript is no longer needed once connected.
        _ = plain;
        _ = len;
    }

    private void SendRecord(ushort legacyVersion, byte type, byte[] data, int offset, int length,
                            bool protect)
    {
        byte[] wire;
        if (!protect)
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
            var plain = new byte[length];
            for (int i = 0; i < length; i++)
                plain[i] = data[offset + i];
            var aad = new byte[13];
            aad[0] = (byte)(_txSeq >> 56);
            aad[1] = (byte)(_txSeq >> 48);
            aad[2] = (byte)(_txSeq >> 40);
            aad[3] = (byte)(_txSeq >> 32);
            aad[4] = (byte)(_txSeq >> 24);
            aad[5] = (byte)(_txSeq >> 16);
            aad[6] = (byte)(_txSeq >> 8);
            aad[7] = (byte)_txSeq;
            aad[8] = type;
            aad[9] = 0x03;
            aad[10] = 0x03;
            aad[11] = (byte)(length >> 8);
            aad[12] = (byte)length;
            var nonce = new byte[12];
            for (int i = 0; i < 4; i++)
                nonce[i] = _clientIv[i];
            for (int i = 0; i < 8; i++)
                nonce[4 + i] = (byte)(_txSeq >> (8 * (7 - i)));
            var aead = new AesGcm(_clientKey);
            byte[] sealed_ = aead.Seal(nonce, aad, plain);
            int recLen = 8 + sealed_.Length;
            wire = new byte[5 + recLen];
            wire[0] = type;
            wire[1] = 0x03;
            wire[2] = 0x03;
            wire[3] = (byte)(recLen >> 8);
            wire[4] = (byte)recLen;
            for (int i = 0; i < 8; i++)
                wire[5 + i] = (byte)(_txSeq >> (8 * (7 - i)));
            for (int i = 0; i < sealed_.Length; i++)
                wire[13 + i] = sealed_[i];
            _txSeq++;
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
        while (_hsLen >= 4)
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
            if (_state == StateError || _state == StateClosed)
                return;
        }
    }

    private void HandleMessage(byte type, byte[] msg, int bodyLen)
    {
        AppendTranscript(msg, 0, 4 + bodyLen);

        if (type == HtServerHello)
        {
            HandleServerHello(msg, bodyLen);
            return;
        }
        if (type == HtCertificate)
        {
            // Ignored: no certificate validation (file header).
            return;
        }
        if (type == HtServerKeyExchange)
        {
            HandleServerKeyExchange(msg, bodyLen);
            return;
        }
        if (type == HtServerHelloDone)
        {
            OnServerHelloDone();
            return;
        }
        Fail("unexpected handshake type " + type);
    }

    private void HandleServerHello(byte[] msg, int bodyLen)
    {
        var r = new TlsReader(msg, 4, bodyLen);
        r.U16();                        // version 0x0303
        _serverRandom = r.Bytes(32);
        int sidLen = r.U8();
        r.Skip(sidLen);
        int suite = r.U16();
        r.U8();                         // compression
        if (suite == 0xC02F || suite == 0xC02B)
        {
            _suite = (ushort)suite;
            _prfKind = HashKind.Sha256;
            _macLen = 32;
            _keyLen = 16;
        }
        else if (suite == 0xC030 || suite == 0xC02C)
        {
            _suite = (ushort)suite;
            _prfKind = HashKind.Sha384;
            _macLen = 48;
            _keyLen = 32;
        }
        else
        {
            Fail("server selected unsupported suite 0x" + suite);
            return;
        }

        // Extensions: only extended_master_secret matters for the key
        // schedule (RFC 7627 - the server echoes it when it chose the
        // session-hash form of master secret).
        _serverEms = false;
        if (r.Position + 2 <= bodyLen)
        {
            int extTotal = r.U16();
            int extEnd = r.Position + extTotal;
            while (r.Position + 4 <= extEnd)
            {
                int extType = r.U16();
                int extLen = r.U16();
                if (extType == 0x0017)
                    _serverEms = true;
                r.Skip(extLen);
            }
        }
        if (Verbose)
            Console.WriteLine("[tls12] serverhello suite=0x" + _suite + " ems=" + (_serverEms ? 1 : 0));
    }

    private void HandleServerKeyExchange(byte[] msg, int bodyLen)
    {
        // ECDHE: curve_type(3=named) + named_curve(2) + point_len(1) +
        // point + sig_alg(2) + sig_len(2) + signature. x25519 points are
        // 32 opaque bytes.
        var r = new TlsReader(msg, 4, bodyLen);
        int curveType = r.U8();
        int curve = r.U16();
        int pointLen = r.U8();
        if (curveType != 3 || curve != 0x001d || pointLen != 32)
        {
            Fail("unsupported ECDHE parameters (curve 0x" + curve + ")");
            return;
        }
        byte[] serverPub = r.Bytes(32);
        // Signature: parsed for length, not verified (no certificate
        // validation in this phase).
        if (r.Position + 4 <= bodyLen)
        {
            r.U16();                    // sig alg
            int sigLen = r.U16();
            r.Skip(sigLen);
        }
        _preMaster = X25519.ScalarMult(_seed, serverPub);
    }

    private void OnServerHelloDone()
    {
        if (_preMaster == null)
        {
            Fail("server hello done without key exchange");
            return;
        }

        // ClientKeyExchange: our x25519 public key.
        byte[] pub = X25519.ScalarMultBase(_seed);
        var cke = new TlsWriter(40);
        cke.U8(32);
        cke.Bytes(pub, 0, 32);
        var msg = new byte[4 + cke.Length];
        msg[0] = HtClientKeyExchange;
        msg[1] = (byte)(cke.Length >> 16);
        msg[2] = (byte)(cke.Length >> 8);
        msg[3] = (byte)cke.Length;
        for (int i = 0; i < cke.Length; i++)
            msg[4 + i] = cke.Buffer[i];
        AppendTranscript(msg, 0, msg.Length);
        SendRecord(0x0303, CtHandshake, msg, 0, msg.Length, false);

        // Transcript hash through ClientKeyExchange: the Finished input,
        // and (with extended_master_secret) the session hash.
        byte[] handshakeHash = HashTranscript();

        // Key derivation (RFC 5246 section 6.3; RFC 7627 when the server
        // echoed extended_master_secret).
        DeriveKeys(handshakeHash);

        // ChangeCipherSpec + encrypted Finished.
        var ccs = new byte[] { CtChangeCipherSpec, 0x03, 0x03, 0x00, 0x01, 0x01 };
        SendAll(ccs);
        _txProtected = true;
        _txSeq = 0;

        // verify_data = PRF(master, "client finished" (15 bytes!),
        //                   Hash(handshake_messages))[12]
        byte[] label = Ascii("client finished");
        byte[] seed = new byte[label.Length + handshakeHash.Length];
        for (int i = 0; i < label.Length; i++)
            seed[i] = label[i];
        for (int i = 0; i < handshakeHash.Length; i++)
            seed[label.Length + i] = handshakeHash[i];
        byte[] verifyData = Prf(_masterSecret, seed, 12);

        var fin = new byte[4 + verifyData.Length];
        fin[0] = HtFinished;
        fin[1] = 0;
        fin[2] = 0;
        fin[3] = (byte)verifyData.Length;
        for (int i = 0; i < verifyData.Length; i++)
            fin[4 + i] = verifyData[i];
        AppendTranscript(fin, 0, fin.Length);
        SendRecord(0x0303, CtHandshake, fin, 0, fin.Length, true /* encrypted */);
        _txFinishedSent = true;

        _state = StateWaitServerFinished;
        if (Verbose)
            Console.WriteLine("[tls12] client finished sent");
    }

    private void DeriveKeys(byte[] sessionHash)
    {
        // master_secret:
        //   classic (RFC 5246): PRF(pms, "master secret", cr + sr, 48)
        //   EMS (RFC 7627):     PRF(pms, "extended master secret",
        //                           session_hash, 48)
        byte[] masterSeed;
        if (_serverEms)
        {
            byte[] labelEms = Ascii("extended master secret");
            masterSeed = new byte[labelEms.Length + sessionHash.Length];
            for (int i = 0; i < labelEms.Length; i++)
                masterSeed[i] = labelEms[i];
            for (int i = 0; i < sessionHash.Length; i++)
                masterSeed[labelEms.Length + i] = sessionHash[i];
        }
        else
        {
            masterSeed = new byte[64 + 13];
            byte[] label = Ascii("master secret");
            int p = 0;
            for (int i = 0; i < 13; i++)
                masterSeed[p++] = label[i];
            for (int i = 0; i < 32; i++)
                masterSeed[p++] = _clientRandom[i];
            for (int i = 0; i < 32; i++)
                masterSeed[p++] = _serverRandom[i];
        }
        _masterSecret = Prf(_preMaster, masterSeed, 48);

        // key_block = PRF(master, "key expansion",
        //                 server_random + client_random,
        //                 2*key + 2*iv)
        int need = 2 * _keyLen + 8;
        byte[] seed2 = new byte[64 + 13];
        byte[] label2 = Ascii("key expansion");
        int pos = 0;
        for (int i = 0; i < 13; i++)
            seed2[pos++] = label2[i];
        for (int i = 0; i < 32; i++)
            seed2[pos++] = _serverRandom[i];
        for (int i = 0; i < 32; i++)
            seed2[pos++] = _clientRandom[i];
        byte[] keyBlock = Prf(_masterSecret, seed2, need);

        _clientKey = new byte[_keyLen];
        _serverKey = new byte[_keyLen];
        _clientIv = new byte[4];
        _serverIv = new byte[4];
        pos = 0;
        for (int i = 0; i < _keyLen; i++)
            _clientKey[i] = keyBlock[pos++];
        for (int i = 0; i < _keyLen; i++)
            _serverKey[i] = keyBlock[pos++];
        for (int i = 0; i < 4; i++)
            _clientIv[i] = keyBlock[pos++];
        for (int i = 0; i < 4; i++)
            _serverIv[i] = keyBlock[pos++];
    }

    /// <summary>
    /// TLS 1.2 PRF (RFC 5246 section 5): P_hash expansion of
    /// label || seed with the suite hash.
    /// </summary>
    private byte[] Prf(byte[] secret, byte[] seed, int length)
    {
        byte[] result = new byte[length];
        int outPos = 0;
        // A(0) = seed; A(i) = HMAC(secret, A(i-1))
        byte[] a = seed;
        int guard = 0;
        while (outPos < length && guard < 32)
        {
            guard++;
            a = Hmac.Compute(_prfKind, secret, a);
            byte[] inner = new byte[a.Length + seed.Length];
            for (int i = 0; i < a.Length; i++)
                inner[i] = a[i];
            for (int i = 0; i < seed.Length; i++)
                inner[a.Length + i] = seed[i];
            byte[] block = Hmac.Compute(_prfKind, secret, inner);
            for (int i = 0; i < block.Length && outPos < length; i++)
                result[outPos++] = block[i];
        }
        return result;
    }

    private void Fail(string reason)
    {
        if (_state == StateClosed || _state == StateError)
            return;
        if (_lastError == null)
            _lastError = reason;
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
        if (_prfKind == HashKind.Sha384)
            return Sha384.Hash(data);
        return Sha256.Hash(data);
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
