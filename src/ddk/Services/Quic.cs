// NeutrinoOS Phase 9: QUIC v1 (RFC 9000/9001) + HTTP/3 (RFC 9114) server.
//
// Scope of this implementation (sufficient for real clients such as
// aioquic and curl --http3):
//   - Initial / Handshake / 1-RTT packet spaces, AES-128-GCM protection
//     (AES-ECB header protection), version 1.
//   - Server-side TLS 1.3 handshake over CRYPTO frames (QuicTlsServer).
//   - Frames: PADDING, PING, ACK, CRYPTO, STREAM, MAX_DATA,
//     MAX_STREAM_DATA, PATH_CHALLENGE/RESPONSE, CONNECTION_CLOSE,
//     HANDSHAKE_DONE. Unknown extension frames are ignored.
//   - Transport parameters (original_destination_connection_id etc.).
//   - HTTP/3: control + QPACK encoder/decoder streams, SETTINGS,
//     request streams with HEADERS/DATA, QPACK static-table codec.
//
// Datagram I/O happens through the NetworkStack UDP queue (port 443).
// The server lives inside WebService.Tick via QuicServer.Pump.
//
// Tier-0 JIT notes: only byte[] state (no 16-byte struct locals/members
// crossing call boundaries), plain int offsets, small helpers.
using System;
using NeutrinoOS.DDK.Crypto;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Stack;

namespace NeutrinoOS.DDK.Services;

public sealed class QuicConnection
{
    // ---- identities ----
    public byte[] InitialDcid = new byte[20];   // client's original DCID (Initial keys)
    public int InitialDcidLen;
    public byte[] MyScid = new byte[8];         // our source connection ID
    public int MyScidLen = 8;
    public byte[] PeerScid = new byte[20];      // client's source CID (our destination)
    public int PeerScidLen;

    public bool Closed;

    // ---- TLS ----
    public QuicTlsServer Tls;

    // ---- packet numbers (server TX) ----
    private ulong _initialPn;
    private ulong _hsPn;
    private ulong _appPn;

    // ---- largest received PN per space (for ACKs); -1 = none ----
    private long _recvInitPn = -1;
    private long _recvHsPn = -1;
    private long _recvAppPn = -1;

    // ---- CRYPTO reassembly per level (in-order) ----
    private byte[] _cryptoInit = new byte[8192];
    private int _cryptoInitLen;
    private byte[] _cryptoHs = new byte[8192];
    private int _cryptoHsLen;

    // ---- handshake progress ----
    private bool _flightSent;
    private bool _hsFinished;
    private bool _initialKeysDone;
    public bool HandshakeDone;

    // ---- outgoing datagrams ----
    public const int MaxOut = 16;
    public byte[][] Out = new byte[MaxOut][];
    public int[] OutLen = new int[MaxOut];
    public int OutCount;

    // ---- HTTP/3 ----
    private bool _h3Started;
    private int _nextUni;                        // next server-initiated uni stream id
    private int _ctrlUni, _encUni, _decUni;
    private ulong _ctrlOff, _encOff, _decOff;
    // client request streams (small table)
    private int[] _sid = new int[4];
    private byte[][] _sbuf = new byte[4][];
    private int[] _slen = new int[4];
    private bool[] _sused = new bool[4];
    private bool[] _sresponded = new bool[4];
    private bool[] _sfin = new bool[4];
    private bool[] _soverflow = new bool[4];

    public QuicConnection(byte[] certDer, byte[] keySeed)
    {
        Tls = new QuicTlsServer(certDer, keySeed);
        _sbuf[0] = new byte[16384];
        _sbuf[1] = new byte[16384];
        _sbuf[2] = new byte[16384];
        _sbuf[3] = new byte[16384];
        for (int i = 0; i < MaxOut; i++)
            Out[i] = new byte[1500];
    }

    public bool MatchesCid(byte[] buf, int off, int len)
    {
        if (len == InitialDcidLen && len > 0)
        {
            bool same = true;
            for (int i = 0; i < len; i++)
            {
                if (buf[off + i] != InitialDcid[i])
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return true;
        }
        if (len == MyScidLen && len > 0)
        {
            bool same = true;
            for (int i = 0; i < len; i++)
            {
                if (buf[off + i] != MyScid[i])
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return true;
        }
        return false;
    }

    // ==================== datagram intake ====================

    public void HandleDatagram(byte[] dg, int len)
    {
        int pos = 0;
        while (pos < len && !Closed)
        {
            int b0 = dg[pos];
            if ((b0 & 0x80) != 0)
            {
                // Long header.
                if (pos + 7 > len)
                    return;
                int version = (dg[pos + 1] << 24) | (dg[pos + 2] << 16) |
                              (dg[pos + 3] << 8) | dg[pos + 4];
                if (version != 1)
                    return;
                int p = pos + 5;
                int dcil = dg[p++];
                if (p + dcil > len)
                    return;
                // First packet: remember the peer's SCID and the DCID.
                if (InitialDcidLen == 0)
                {
                    InitialDcidLen = dcil;
                    for (int i = 0; i < dcil; i++)
                        InitialDcid[i] = dg[p + i];
                }
                p += dcil;
                if (p >= len)
                    return;
                int scil = dg[p++];
                if (p + scil > len)
                    return;
                if (scil >= 4)
                {
                    PeerScidLen = scil > 20 ? 20 : scil;
                    for (int i = 0; i < PeerScidLen; i++)
                        PeerScid[i] = dg[p + i];
                }
                p += scil;

                int type = (b0 >> 4) & 3;
                if (type == 0)
                {
                    // First Initial: derive the Initial keys NOW - they are
                    // needed to decrypt this very packet. (Deriving them in
                    // the ClientHello handler was a chicken-and-egg bug.)
                    if (!_initialKeysDone)
                    {
                        Tls.InitInitialKeys(InitialDcid, 0, InitialDcidLen);
                        _initialKeysDone = true;
                    }                    // Initial: token.
                    int tokenLen;
                    p = ReadVarint(dg, p, len, out tokenLen);
                    if (p < 0)
                        return;
                    p += tokenLen;
                }
                else if (type == 1 || type == 3)
                {
                    return;   // 0-RTT / Retry: not supported
                }
                int payLen;
                p = ReadVarint(dg, p, len, out payLen);
                if (p < 0 || p + payLen > len || payLen < 1)
                    return;
                int pnOffset = p;
                int level = type == 0 ? 0 : 2;
                if (!ProcessPacket(dg, pos, pnOffset, p + payLen, level, true))
                    return;
                // Advancing by payLen would double-count the header; recompute
                // from the length field: next packet starts at p + payLen.
                pos = p + payLen;
            }
            else
            {
                // Short header (1-RTT).
                if (dg[pos] == 0)
                    return;   // trailing zero padding after the last packet
                int pnOffset = pos + 1 + MyScidLen;
                if (pnOffset >= len)
                    return;
                if (!ProcessPacket(dg, pos, pnOffset, len, 3, false))
                    return;
                return;   // short header packet extends to the end
            }
        }
    }

    /// <summary>
    /// Decrypt and process one packet. level: 0 initial, 2 handshake,
    /// 3 application. Returns false when the packet is malformed.
    /// </summary>
    private bool ProcessPacket(byte[] dg, int start, int pnOffset, int end, int level, bool longHeader)
    {
        byte[] hpKey;
        byte[] key;
        byte[] iv;
        if (level == 0)
        {
            // Receiving the peer's packets: CLIENT keys (server keys are
            // for our own outgoing protection).
            hpKey = Tls.ClientInitialHp;
            key = Tls.ClientInitialKey;
            iv = Tls.ClientInitialIv;
        }
        else if (level == 2)
        {
            hpKey = Tls.ClientHsHp;
            key = Tls.ClientHsKey;
            iv = Tls.ClientHsIv;
        }
        else
        {
            hpKey = Tls.ClientAppHp;
            key = Tls.ClientAppKey;
            iv = Tls.ClientAppIv;
        }

        if (pnOffset + 4 + 16 > end)
            return false;

        // Header protection sample: 16 bytes at pnOffset + 4.
        var sample = new byte[16];
        for (int i = 0; i < 16; i++)
            sample[i] = dg[pnOffset + 4 + i];
        var hpc = new Aes(hpKey);
        hpc.EncryptBlock(sample, 0);

        // Unmask the first byte first - the packet number LENGTH lives in
        // its low bits (RFC 9001 5.4). Reading pnLen from the PN bytes was
        // the original bug here.
        int mask0 = longHeader ? 0x0F : 0x1F;
        int b0 = dg[start] ^ (sample[0] & mask0);
        int pnLen = (b0 & 0x03) + 1;
        if (pnOffset + pnLen + 16 > end)
            return false;

        ulong pn = 0;
        var pnBytes = new byte[4];
        for (int i = 0; i < pnLen; i++)
        {
            pnBytes[i] = (byte)(dg[pnOffset + i] ^ sample[1 + i]);
            pn = (pn << 8) | pnBytes[i];
        }

        // Reconstruct AAD = header bytes with unmasked first byte/PN.
        int headerLen = pnOffset + pnLen - start;
        var aad = new byte[headerLen];
        for (int i = 0; i < headerLen; i++)
            aad[i] = dg[start + i];
        aad[0] = (byte)b0;
        for (int i = 0; i < pnLen; i++)
            aad[pnOffset - start + i] = pnBytes[i];

        // Nonce.
        var nonce = new byte[12];
        for (int i = 0; i < 12; i++)
            nonce[i] = iv[i];
        // XOR the packet number into the low 8 bytes (unrolled constant
        // shifts - see X25519.Store64 for the Tier-0 JIT variable-shift
        // hazard these writes used to have).
        nonce[11] ^= (byte)pn;
        nonce[10] ^= (byte)(pn >> 8);
        nonce[9] ^= (byte)(pn >> 16);
        nonce[8] ^= (byte)(pn >> 24);
        nonce[7] ^= (byte)(pn >> 32);
        nonce[6] ^= (byte)(pn >> 40);
        nonce[5] ^= (byte)(pn >> 48);
        nonce[4] ^= (byte)(pn >> 56);

        int ctStart = pnOffset + pnLen;
        int ctLen = end - ctStart - 16;
        if (ctLen < 0)
            return false;
        var tag = new byte[16];
        for (int i = 0; i < 16; i++)
            tag[i] = dg[end - 16 + i];

        var aead = new AesGcm(key);
        byte[] plain;
        if (!aead.Open(nonce, aad, dg, ctStart, ctLen, tag, out plain))
            return false;

        if (level == 0)
        {
            if ((long)pn > _recvInitPn)
                _recvInitPn = (long)pn;
        }
        else if (level == 2)
        {
            if ((long)pn > _recvHsPn)
                _recvHsPn = (long)pn;
        }
        else
        {
            if ((long)pn > _recvAppPn)
                _recvAppPn = (long)pn;
        }

        ProcessFrames(plain, ctLen, level);
        return true;
    }

    private void ProcessFrames(byte[] buf, int len, int level)
    {
        int pos = 0;
        while (pos < len && !Closed)
        {
            int type;
            pos = ReadVarint(buf, pos, len, out type);
            if (pos < 0)
                return;

            if (type == 0x00 || type == 0x01)
            {
                continue;                          // PADDING / PING
            }
            if (type == 0x02 || type == 0x03)
            {
                // ACK: largest, delay, rangeCount, firstRange, [ranges]
                int v;
                pos = ReadVarint(buf, pos, len, out v);
                if (pos < 0)
                    return;
                pos = ReadVarint(buf, pos, len, out v);
                if (pos < 0)
                    return;
                int rangeCount;
                pos = ReadVarint(buf, pos, len, out rangeCount);
                if (pos < 0)
                    return;
                pos = ReadVarint(buf, pos, len, out v);
                if (pos < 0)
                    return;
                if (type == 0x03)
                {
                    int ec;
                    pos = ReadVarint(buf, pos, len, out ec);
                    if (pos < 0)
                        return;
                    pos = ReadVarint(buf, pos, len, out ec);
                    if (pos < 0)
                        return;
                    pos = ReadVarint(buf, pos, len, out ec);
                    if (pos < 0)
                        return;
                }
                for (int i = 0; i < rangeCount && pos >= 0; i++)
                {
                    pos = ReadVarint(buf, pos, len, out v);
                    if (pos < 0)
                        return;
                    pos = ReadVarint(buf, pos, len, out v);
                    if (pos < 0)
                        return;
                }
                if (pos < 0 || pos > len)
                    return;
                continue;
            }
            if (type == 0x06)
            {
                // CRYPTO: offset, length, data
                int offset, flen;
                pos = ReadVarint(buf, pos, len, out offset);
                if (pos < 0)
                    return;
                pos = ReadVarint(buf, pos, len, out flen);
                if (pos < 0 || pos + flen > len)
                    return;
                HandleCrypto(level, buf, pos, flen, offset);
                pos += flen;
                continue;
            }
            if (type >= 0x08 && type <= 0x0F)
            {
                // STREAM: id, [offset], [length], data
                int id;
                pos = ReadVarint(buf, pos, len, out id);
                if (pos < 0)
                    return;
                int offset = 0;
                if ((type & 0x04) != 0)
                {
                    pos = ReadVarint(buf, pos, len, out offset);
                    if (pos < 0)
                        return;
                }
                int slen = len - pos;
                if ((type & 0x02) != 0)
                {
                    pos = ReadVarint(buf, pos, len, out slen);
                    if (pos < 0 || pos + slen > len)
                        return;
                }
                bool fin = (type & 0x01) != 0;
                HandleStream(level, id, buf, pos, slen, fin);
                pos += slen;
                continue;
            }
            if (type == 0x10 || type == 0x11)
            {
                int v;
                if (type == 0x11)
                {
                    pos = ReadVarint(buf, pos, len, out v);
                    if (pos < 0)
                        return;
                }
                pos = ReadVarint(buf, pos, len, out v);
                if (pos < 0)
                    return;
                continue;
            }
            if (type == 0x12 || type == 0x13 || type == 0x14 || type == 0x15)
            {
                int v;
                pos = ReadVarint(buf, pos, len, out v);
                if (pos < 0)
                    return;
                continue;
            }
            if (type == 0x18)
            {
                // NEW_CONNECTION_ID: seq, retirePrior, len(1), cid, token(16)
                int v;
                pos = ReadVarint(buf, pos, len, out v);
                if (pos < 0)
                    return;
                pos = ReadVarint(buf, pos, len, out v);
                if (pos < 0 || pos >= len)
                    return;
                int cl = buf[pos++];
                pos += cl + 16;
                if (pos > len)
                    return;
                continue;
            }
            if (type == 0x1A)
            {
                // PATH_CHALLENGE: echo 8 bytes back in a PATH_RESPONSE.
                if (pos + 8 > len)
                    return;
                SendPathResponse(buf, pos);
                pos += 8;
                continue;
            }
            if (type == 0x1B)
            {
                pos += 8;
                if (pos > len)
                    return;
                continue;
            }
            if (type == 0x1C || type == 0x1D)
            {
                Closed = true;
                return;
            }
            if (type == 0x1E)
            {
                continue;   // HANDSHAKE_DONE (client can't send; tolerate)
            }
            // Unknown frame: cannot be skipped safely (varint lengths vary).
            return;
        }
    }

    private void HandleCrypto(int level, byte[] buf, int off, int len, int offset)
    {
        if (level == 0)
        {
            if (offset == _cryptoInitLen && _cryptoInitLen + len <= _cryptoInit.Length)
            {
                for (int i = 0; i < len; i++)
                    _cryptoInit[_cryptoInitLen + i] = buf[off + i];
                _cryptoInitLen += len;
            }
            TryClientHello();
        }
        else if (level == 2)
        {
            if (offset == _cryptoHsLen && _cryptoHsLen + len <= _cryptoHs.Length)
            {
                for (int i = 0; i < len; i++)
                    _cryptoHs[_cryptoHsLen + i] = buf[off + i];
                _cryptoHsLen += len;
            }
            TryClientFinished();
        }
    }

    private void TryClientHello()
    {
        if (_flightSent || _cryptoInitLen < 4)
            return;
        int bodyLen = (_cryptoInit[1] << 16) | (_cryptoInit[2] << 8) | _cryptoInit[3];
        if (_cryptoInit[0] != 1 || _cryptoInitLen < 4 + bodyLen)
            return;

        // Server transport parameters (encoded by the caller config below).
        var tp = new byte[256];
        int tpLen = BuildTransportParameters(tp);
        if (!Tls.ProcessClientHello(_cryptoInit, 0, _cryptoInitLen, tp, tpLen))
        {
            Closed = true;
            return;
        }
        _flightSent = true;
        SendInitialFlight();
    }

    private void TryClientFinished()
    {
        if (!_flightSent || _hsFinished || _cryptoHsLen < 4)
            return;
        int bodyLen = (_cryptoHs[1] << 16) | (_cryptoHs[2] << 8) | _cryptoHs[3];
        if (_cryptoHs[0] != 20 || _cryptoHsLen < 4 + bodyLen)
            return;
        if (!Tls.ProcessClientFinished(_cryptoHs, 0, _cryptoHsLen, true))
        {
            Closed = true;
            return;
        }
        _hsFinished = true;
        SendAppFlight();
    }

    // ==================== HTTP/3 stream handling ====================

    private void HandleStream(int level, int id, byte[] buf, int off, int len, bool fin)
    {
        if (level != 3)
            return;
        if ((id & 0x03) == 0x02 || (id & 0x03) == 0x03)
            return;                        // client uni streams: ignore
        int slot = -1;
        for (int i = 0; i < 4; i++)
        {
            if (_sused[i] && _sid[i] == id)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if (!_sused[i])
                {
                    slot = i;
                    _sused[i] = true;
                    _sid[i] = id;
                    _slen[i] = 0;
                    _sresponded[i] = false;
                    _sfin[i] = false;
                    _soverflow[i] = false;
                    break;
                }
            }
        }
        if (slot < 0)
            return;
        if (_slen[slot] + len <= _sbuf[slot].Length)
        {
            for (int i = 0; i < len; i++)
                _sbuf[slot][_slen[slot] + i] = buf[off + i];
            _slen[slot] += len;
        }
        else
        {
            _soverflow[slot] = true;   // request larger than the buffer -> 413
        }
        if (fin)
            _sfin[slot] = true;
        if (!_sresponded[slot])
            TryRespond(slot);
    }

    /// <summary>Request bodies are bounded by the stream buffer (minus the
    /// HEADERS frame); bigger requests are answered with 413.</summary>
    private const int MaxH3Body = 15000;

    /// <summary>
    /// Parse the buffered request frames (HEADERS + DATA) and answer the
    /// request once the client closed its side of the stream (FIN). DATA
    /// frame payloads are collected as the request body; trailer HEADERS
    /// and unknown frame types are skipped.
    /// </summary>
    private void TryRespond(int slot)
    {
        if (!_sfin[slot])
            return;                        // wait for the request to end
        byte[] data = _sbuf[slot];
        int len = _slen[slot];
        if (len < 2)
            return;
        int pos = 0;
        int frameType;
        pos = ReadVarint(data, pos, len, out frameType);
        if (pos < 0 || frameType != 0x01)
            return;                        // only HEADERS first
        int blockLen;
        pos = ReadVarint(data, pos, len, out blockLen);
        if (pos < 0 || pos + blockLen > len)
            return;                        // wait for the full field section

        var names = new string[32];
        var values = new string[32];
        int count = Qpack.DecodeFieldSection(data, pos, blockLen, names, values, 32);
        if (count < 0)
            return;
        pos += blockLen;

        string method = null;
        string path = null;
        for (int i = 0; i < count; i++)
        {
            if (Qpack.StrEq(names[i], ":method"))
                method = values[i];
            else if (Qpack.StrEq(names[i], ":path"))
                path = values[i];
        }
        if (path == null)
            return;

        var bodyChars = new char[MaxH3Body];
        int bodyOff = 0;
        bool tooLarge = _soverflow[slot];
        while (pos < len)
        {
            int ft;
            int fp = ReadVarint(data, pos, len, out ft);
            if (fp < 0)
                break;
            int fl;
            fp = ReadVarint(data, fp, len, out fl);
            if (fp < 0 || fp + fl > len)
                break;
            if (ft == 0x00)   // DATA
            {
                for (int i = 0; i < fl && !tooLarge; i++)
                {
                    if (bodyOff >= bodyChars.Length)
                    {
                        tooLarge = true;
                        break;
                    }
                    bodyChars[bodyOff++] = (char)data[fp + i];
                }
            }
            pos = fp + fl;
        }
        string reqBody = "";
        if (!tooLarge && bodyOff > 0)
        {
            var chars = new char[bodyOff];
            for (int i = 0; i < bodyOff; i++)
                chars[i] = bodyChars[i];
            reqBody = new string(chars);
        }
        _sresponded[slot] = true;

        int status;
        string statusText;
        string contentType;
        string body;
        if (tooLarge)
        {
            status = 413;
            statusText = "Payload Too Large";
            contentType = "text/plain";
            body = "payload too large\n";
        }
        else
        {
            WebService.BuildRoute(method == null ? "GET" : method, path, reqBody, out status, out statusText, out contentType, out body);
        }

        // Response field section.
        var rnames = new string[4];
        var rvalues = new string[4];
        rnames[0] = ":status";
        rvalues[0] = IntToStr(status);
        rnames[1] = "content-type";
        rvalues[1] = contentType;
        rnames[2] = "content-length";
        rvalues[2] = IntToStr(body.Length);
        rnames[3] = "server";
        rvalues[3] = "NeutrinoOS";
        var block = new byte[1024];
        int blockEnd = Qpack.EncodeFieldSection(block, 0, rnames, rvalues, 4);

        // HEADERS h3 frame (0x01) then DATA h3 frame (0x00), each in a
        // QUIC STREAM frame with cumulative offsets.
        var h3 = new byte[16384];
        int hn = 0;
        hn = WriteVarint(h3, hn, 0x01);
        hn = WriteVarint(h3, hn, blockEnd);
        for (int i = 0; i < blockEnd; i++)
            h3[hn++] = block[i];
        hn = WriteVarint(h3, hn, 0x00);
        hn = WriteVarint(h3, hn, body.Length);
        for (int i = 0; i < body.Length; i++)
            h3[hn++] = (byte)body[i];

        SendStreamData(_sid[slot], h3, hn);
    }

    // ==================== outbound ====================

    private int BuildTransportParameters(byte[] tp)
    {
        int n = 0;
        // original_destination_connection_id (0x00)
        n = WriteVarint(tp, n, 0x00);
        n = WriteVarint(tp, n, InitialDcidLen);
        for (int i = 0; i < InitialDcidLen; i++)
            tp[n++] = InitialDcid[i];
        // max_idle_timeout (0x01) = 30000ms
        n = WriteVarint(tp, n, 0x01);
        n = WriteVarint(tp, n, 4);
        n = WriteVarint(tp, n, 30000);
        // max_udp_payload_size (0x03) = 1472
        n = WriteVarint(tp, n, 0x03);
        n = WriteVarint(tp, n, 2);
        n = WriteVarint(tp, n, 1472);
        // initial_max_data (0x04) = 1 MiB
        n = WriteVarint(tp, n, 0x04);
        n = WriteVarint(tp, n, 4);
        n = WriteVarint(tp, n, 1048576);
        // initial_max_stream_data_bidi_local (0x05) = 256 KiB
        n = WriteVarint(tp, n, 0x05);
        n = WriteVarint(tp, n, 4);
        n = WriteVarint(tp, n, 262144);
        // initial_max_stream_data_bidi_remote (0x06) = 256 KiB
        n = WriteVarint(tp, n, 0x06);
        n = WriteVarint(tp, n, 4);
        n = WriteVarint(tp, n, 262144);
        // initial_max_stream_data_uni (0x07) = 256 KiB
        n = WriteVarint(tp, n, 0x07);
        n = WriteVarint(tp, n, 4);
        n = WriteVarint(tp, n, 262144);
        // initial_max_streams_bidi (0x08) = 16
        n = WriteVarint(tp, n, 0x08);
        n = WriteVarint(tp, n, 1);
        n = WriteVarint(tp, n, 16);
        // initial_max_streams_uni (0x09) = 16
        n = WriteVarint(tp, n, 0x09);
        n = WriteVarint(tp, n, 1);
        n = WriteVarint(tp, n, 16);
        // disable_active_migration (0x0c)
        n = WriteVarint(tp, n, 0x0C);
        n = WriteVarint(tp, n, 0);
        // active_connection_id_limit (0x0e) = 4
        n = WriteVarint(tp, n, 0x0E);
        n = WriteVarint(tp, n, 1);
        n = WriteVarint(tp, n, 4);
        // initial_source_connection_id (0x0f)
        n = WriteVarint(tp, n, 0x0F);
        n = WriteVarint(tp, n, MyScidLen);
        for (int i = 0; i < MyScidLen; i++)
            tp[n++] = MyScid[i];
        return n;
    }

    private void SendInitialFlight()
    {
        // ---- Initial packet payload: ACK + CRYPTO(ServerHello) ----
        var p1 = new byte[1300];
        int n1 = 0;
        n1 = AppendAckFrame(p1, n1, _recvInitPn);
        n1 = WriteVarint(p1, n1, 0x06);
        n1 = WriteVarint(p1, n1, 0);
        n1 = WriteVarint(p1, n1, Tls.OutInitialLen);
        for (int i = 0; i < Tls.OutInitialLen; i++)
            p1[n1++] = Tls.OutInitial[i];

        // ---- Handshake packet payload: CRYPTO(EE..Finished) ----
        var p2 = new byte[1400];
        int n2 = 0;
        n2 = WriteVarint(p2, n2, 0x06);
        n2 = WriteVarint(p2, n2, 0);
        n2 = WriteVarint(p2, n2, Tls.OutHandshakeLen);
        for (int i = 0; i < Tls.OutHandshakeLen; i++)
            p2[n2++] = Tls.OutHandshake[i];

        // Packet size estimates (for Initial padding to 1200 total):
        // long header = 1 + 4 + 1 + dcid + 1 + scid + [tokenLen 1] + len(2)
        int dcil = PeerScidLen > 0 ? PeerScidLen : InitialDcidLen;
        int hdrLen = 1 + 4 + 1 + dcil + 1 + MyScidLen + 1 + 2;
        int p1Total = hdrLen + 4 + n1 + 16;
        int p2Total = hdrLen + 4 + n2 + 16;
        // Pad to >= 1200 (with margin): clients drop server Initial
        // datagrams below 1200 bytes (RFC 9000 14.1).
        int need = 1240 - (p1Total + p2Total);
        for (int i = 0; i < need; i++)
            p1[n1++] = 0x00;                 // PADDING frames

        var dg = new byte[1500];
        int dn = 0;
        dn = BuildLongPacket(dg, dn, 0, p1, n1);
        dn = BuildLongPacket(dg, dn, 2, p2, n2);
        Queue(dg, dn);
        if (_initialPn == 0)
            _initialPn = 1;                  // consumed pn 0
    }

    private void SendAppFlight()
    {
        // ACK the client's Handshake packet in a Handshake packet, then
        // 1-RTT: HANDSHAKE_DONE + HTTP/3 boilerplate streams.
        var hs = new byte[64];
        int hn = 0;
        hn = AppendAckFrame(hs, hn, _recvHsPn);

        var app = new byte[1000];
        int an = 0;
        if (_recvAppPn >= 0)
            an = AppendAckFrame(app, an, _recvAppPn);
        an = WriteVarint(app, an, 0x1E);     // HANDSHAKE_DONE

        // Server control stream (id 3): type 0x00 + SETTINGS.
        int ctrl = _nextUni + 3;             // 3, 7, 11...
        _nextUni += 4;
        _ctrlUni = ctrl;
        var cbytes = new byte[64];
        int cn = 0;
        cn = WriteVarint(cbytes, cn, 0x00);  // stream type: control
        cn = WriteVarint(cbytes, cn, 0x04);  // SETTINGS frame
        cn = WriteVarint(cbytes, cn, 4);     // payload: two zero settings
        cn = WriteVarint(cbytes, cn, 0x01);  // QPACK_MAX_TABLE_CAPACITY
        cn = WriteVarint(cbytes, cn, 0);
        cn = WriteVarint(cbytes, cn, 0x07);  // QPACK_BLOCKED_STREAMS
        cn = WriteVarint(cbytes, cn, 0);
        an = AppendStreamFrame(app, an, ctrl, 0, cbytes, cn, false);
        _ctrlOff = (ulong)cn;

        int enc = _nextUni + 3;
        _nextUni += 4;
        _encUni = enc;
        var ebytes = new byte[4];
        int en = 0;
        en = WriteVarint(ebytes, en, 0x02);  // stream type: QPACK encoder
        an = AppendStreamFrame(app, an, enc, 0, ebytes, en, false);
        _encOff = (ulong)en;

        int dec = _nextUni + 3;
        _nextUni += 4;
        _decUni = dec;
        var dbytes = new byte[4];
        int dn2 = 0;
        dn2 = WriteVarint(dbytes, dn2, 0x03); // stream type: QPACK decoder
        an = AppendStreamFrame(app, an, dec, 0, dbytes, dn2, false);
        _decOff = (ulong)dn2;

        _h3Started = true;

        var dg = new byte[1500];
        int pos = 0;
        pos = BuildLongPacket(dg, pos, 2, hs, hn);
        pos = BuildShortPacket(dg, pos, app, an);
        Queue(dg, pos);
    }

    private void SendPathResponse(byte[] buf, int off)
    {
        var app = new byte[32];
        int n = 0;
        if (_recvAppPn >= 0)
            n = AppendAckFrame(app, n, _recvAppPn);
        n = WriteVarint(app, n, 0x1B);
        for (int i = 0; i < 8; i++)
            app[n++] = buf[off + i];
        var dg = new byte[1500];
        int pos = BuildShortPacket(dg, 0, app, n);
        Queue(dg, pos);
    }

    private void SendStreamData(int streamId, byte[] data, int len)
    {
        var app = new byte[16384];
        int n = 0;
        if (_recvAppPn >= 0)
            n = AppendAckFrame(app, n, _recvAppPn);
        n = AppendStreamFrame(app, n, streamId, 0, data, len, true);
        var dg = new byte[16600];
        int pos = BuildShortPacket(dg, 0, app, n);
        Queue(dg, pos);
    }

    // ==================== packet builders ====================

    private int BuildLongPacket(byte[] dg, int pos, int level, byte[] payload, int payloadLen)
    {
        byte[] key = level == 0 ? Tls.ServerInitialKey : Tls.ServerHsKey;
        byte[] iv = level == 0 ? Tls.ServerInitialIv : Tls.ServerHsIv;
        byte[] hp = level == 0 ? Tls.ServerInitialHp : Tls.ServerHsHp;
        ulong pn = level == 0 ? _initialPn : _hsPn;
        int type = level == 0 ? 0 : 2;

        int start = pos;
        dg[pos++] = (byte)(0xC0 | (type << 4) | 0x03);   // long, pnlen 4
        dg[pos++] = 0;
        dg[pos++] = 0;
        dg[pos++] = 0;
        dg[pos++] = 1;                                   // version 1
        int dcil = PeerScidLen > 0 ? PeerScidLen : InitialDcidLen;
        dg[pos++] = (byte)dcil;
        for (int i = 0; i < dcil; i++)
            dg[pos++] = (byte)(PeerScidLen > 0 ? PeerScid[i] : InitialDcid[i]);
        dg[pos++] = (byte)MyScidLen;
        for (int i = 0; i < MyScidLen; i++)
            dg[pos++] = MyScid[i];
        if (level == 0)
            dg[pos++] = 0;                               // token length 0
        int lenPos = pos;
        pos = WriteVarint(dg, pos, (ulong)(4 + payloadLen + 16));
        int pnPos = pos;
        dg[pos++] = (byte)(pn >> 24);
        dg[pos++] = (byte)(pn >> 16);
        dg[pos++] = (byte)(pn >> 8);
        dg[pos++] = (byte)pn;

        // AEAD.
        var aad = new byte[pos - start];
        for (int i = 0; i < aad.Length; i++)
            aad[i] = dg[start + i];
        var nonce = new byte[12];
        for (int i = 0; i < 12; i++)
            nonce[i] = iv[i];
        // XOR the packet number into the low 8 bytes (unrolled constant
        // shifts - see X25519.Store64 for the Tier-0 JIT variable-shift
        // hazard these writes used to have).
        nonce[11] ^= (byte)pn;
        nonce[10] ^= (byte)(pn >> 8);
        nonce[9] ^= (byte)(pn >> 16);
        nonce[8] ^= (byte)(pn >> 24);
        nonce[7] ^= (byte)(pn >> 32);
        nonce[6] ^= (byte)(pn >> 40);
        nonce[5] ^= (byte)(pn >> 48);
        nonce[4] ^= (byte)(pn >> 56);
        var plain = new byte[payloadLen];
        for (int i = 0; i < payloadLen; i++)
            plain[i] = payload[i];
        var aead = new AesGcm(key);
        byte[] sealed_ = aead.Seal(nonce, aad, plain);
        for (int i = 0; i < sealed_.Length; i++)
            dg[pos++] = sealed_[i];

        // Header protection.
        var sample = new byte[16];
        for (int i = 0; i < 16; i++)
            sample[i] = dg[pnPos + 4 + i];
        var hpc = new Aes(hp);
        hpc.EncryptBlock(sample, 0);
        dg[start] ^= (byte)(sample[0] & 0x0F);
        dg[pnPos] ^= sample[1];
        dg[pnPos + 1] ^= sample[2];
        dg[pnPos + 2] ^= sample[3];
        dg[pnPos + 3] ^= sample[4];

        if (level == 0)
            _initialPn++;
        else
            _hsPn++;
        return pos;
    }

    private int BuildShortPacket(byte[] dg, int pos, byte[] payload, int payloadLen)
    {
        int start = pos;
        dg[pos++] = (byte)(0x40 | 0x03);                 // short, pnlen 4
        for (int i = 0; i < MyScidLen; i++)
            dg[pos++] = (byte)(i < PeerScidLen ? PeerScid[i] : MyScid[i]);
        int pnPos = pos;
        ulong pn = _appPn;
        dg[pos++] = (byte)(pn >> 24);
        dg[pos++] = (byte)(pn >> 16);
        dg[pos++] = (byte)(pn >> 8);
        dg[pos++] = (byte)pn;

        var aad = new byte[pos - start];
        for (int i = 0; i < aad.Length; i++)
            aad[i] = dg[start + i];
        var nonce = new byte[12];
        for (int i = 0; i < 12; i++)
            nonce[i] = Tls.ServerAppIv[i];
        // XOR the packet number into the low 8 bytes (unrolled constant
        // shifts - see X25519.Store64 for the Tier-0 JIT variable-shift
        // hazard these writes used to have).
        nonce[11] ^= (byte)pn;
        nonce[10] ^= (byte)(pn >> 8);
        nonce[9] ^= (byte)(pn >> 16);
        nonce[8] ^= (byte)(pn >> 24);
        nonce[7] ^= (byte)(pn >> 32);
        nonce[6] ^= (byte)(pn >> 40);
        nonce[5] ^= (byte)(pn >> 48);
        nonce[4] ^= (byte)(pn >> 56);
        var plain = new byte[payloadLen];
        for (int i = 0; i < payloadLen; i++)
            plain[i] = payload[i];
        var aead = new AesGcm(Tls.ServerAppKey);
        byte[] sealed_ = aead.Seal(nonce, aad, plain);
        for (int i = 0; i < sealed_.Length; i++)
            dg[pos++] = sealed_[i];

        var sample = new byte[16];
        for (int i = 0; i < 16; i++)
            sample[i] = dg[pnPos + 4 + i];
        var hpc = new Aes(Tls.ServerAppHp);
        hpc.EncryptBlock(sample, 0);
        dg[start] ^= (byte)(sample[0] & 0x1F);
        dg[pnPos] ^= sample[1];
        dg[pnPos + 1] ^= sample[2];
        dg[pnPos + 2] ^= sample[3];
        dg[pnPos + 3] ^= sample[4];
        _appPn++;
        return pos;
    }

    private void Queue(byte[] source, int len)
    {
        if (OutCount >= MaxOut)
            return;
        var copy = new byte[len];
        for (int i = 0; i < len; i++)
            copy[i] = source[i];
        Out[OutCount] = copy;
        OutLen[OutCount] = len;
        OutCount++;
    }

    // ==================== small helpers ====================

    private static int AppendAckFrame(byte[] buf, int pos, long largestPn)
    {
        if (largestPn < 0)
            return pos;
        pos = WriteVarint(buf, pos, 0x02);
        pos = WriteVarint(buf, pos, (ulong)largestPn);
        pos = WriteVarint(buf, pos, 0);      // ack delay
        pos = WriteVarint(buf, pos, 0);      // range count
        pos = WriteVarint(buf, pos, 0);      // first range
        return pos;
    }

    private static int AppendStreamFrame(byte[] buf, int pos, int id, long offset,
        byte[] data, int len, bool fin)
    {
        // 0x08 type + 0x04 offset + 0x02 length (+0x01 FIN).
        int type = 0x0E | (fin ? 0x01 : 0x00);
        pos = WriteVarint(buf, pos, (ulong)type);
        pos = WriteVarint(buf, pos, (ulong)id);
        pos = WriteVarint(buf, pos, (ulong)offset);
        pos = WriteVarint(buf, pos, (ulong)len);
        for (int i = 0; i < len; i++)
            buf[pos++] = data[i];
        return pos;
    }

    private static int WriteVarint(byte[] buf, int pos, int value)
    {
        return WriteVarint(buf, pos, (ulong)value);
    }

    private static int WriteVarint(byte[] buf, int pos, ulong value)
    {
        if (value < 64)
        {
            buf[pos] = (byte)value;
            return pos + 1;
        }
        if (value < 16384)
        {
            buf[pos] = (byte)(0x40 | (value >> 8));
            buf[pos + 1] = (byte)value;
            return pos + 2;
        }
        if (value < 1073741824)
        {
            buf[pos] = (byte)(0x80 | (value >> 24));
            buf[pos + 1] = (byte)(value >> 16);
            buf[pos + 2] = (byte)(value >> 8);
            buf[pos + 3] = (byte)value;
            return pos + 4;
        }
        buf[pos] = (byte)(0xC0 | (value >> 56));
        buf[pos + 1] = (byte)(value >> 48);
        buf[pos + 2] = (byte)(value >> 40);
        buf[pos + 3] = (byte)(value >> 32);
        buf[pos + 4] = (byte)(value >> 24);
        buf[pos + 5] = (byte)(value >> 16);
        buf[pos + 6] = (byte)(value >> 8);
        buf[pos + 7] = (byte)value;
        return pos + 8;
    }

    private static int ReadVarint(byte[] buf, int pos, int end, out int value)
    {
        value = 0;
        if (pos >= end)
            return -1;
        int b = buf[pos];
        int len = 1 << (b >> 6);
        if (pos + len > end)
            return -1;
        ulong v = (ulong)(b & 0x3F);
        for (int i = 1; i < len; i++)
            v = (v << 8) | buf[pos + i];
        value = (int)v;
        return pos + len;
    }

    private static string IntToStr(int value)
    {
        // Mask to the declared 32 bits first (see WebService.IntToStr).
        long v = (long)value & 0xFFFFFFFFL;
        if (v >= 0x80000000L)
            v -= 0x100000000L;
        if (v == 0)
            return "0";
        bool neg = v < 0;
        if (neg)
            v = -v;
        var digits = new char[12];
        int n = 0;
        while (v > 0)
        {
            digits[n++] = (char)('0' + (int)(v % 10));
            v /= 10;
        }
        if (neg)
            digits[n++] = '-';
        var result = new char[n];
        for (int i = 0; i < n; i++)
            result[i] = digits[n - 1 - i];
        return new string(result);
    }
}

/// <summary>
/// UDP front end for QuicConnection: pulls datagrams from the stack's
/// UDP queue (port 443) and writes responses back through SendUdp /
/// SendUdp6. Driven from WebService.Tick.
/// </summary>
public sealed unsafe class QuicServer
{
    private QuicConnection _conn;
    private readonly byte[] _certDer;
    private readonly byte[] _keySeed;
    private readonly byte[] _rx = new byte[1600];

    public QuicServer(byte[] certDer, byte[] keySeed)
    {
        _certDer = certDer;
        _keySeed = keySeed;
    }

    public void Pump(NetworkStack stack)
    {
        for (int i = 0; i < 8; i++)
        {
            int n;
            uint srcIp;
            ushort srcPort;
            bool v6 = false;
            Ipv6Address srcAddr = default;
            fixed (byte* p = _rx)
            {
                n = stack.ReceiveUdpTo(443, out srcIp, out srcPort, p, _rx.Length);
                if (n == 0)
                {
                    n = stack.ReceiveUdp6To(443, out srcAddr, out srcPort, p, _rx.Length);
                    v6 = n > 0;
                }
            }
            if (n <= 0)
                break;

            if (_conn == null || _conn.Closed)
            {
                _conn = new QuicConnection(_certDer, _keySeed);
                var scid = Csprng.GetBytes(8);
                for (int k = 0; k < 8; k++)
                    _conn.MyScid[k] = scid[k];
            }
            _conn.HandleDatagram(_rx, n);

            // Flush the response datagrams.
            for (int q = 0; q < _conn.OutCount; q++)
            {
                fixed (byte* p = _conn.Out[q])
                {
                    if (v6)
                    {
                        var addr = srcAddr;
                        stack.SendUdp6(&addr, 443, srcPort, p, _conn.OutLen[q]);
                    }
                    else
                    {
                        stack.SendUdp(srcIp, 443, srcPort, p, _conn.OutLen[q]);
                    }
                }
            }
            _conn.OutCount = 0;
            if (_conn.Closed)
                _conn = null;
        }
    }
}
