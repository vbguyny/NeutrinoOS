// NeutrinoOS DDK - HTTP/2 server connection (RFC 9113) - Phase 9 Task 3
//
// Framing layer (DATA/HEADERS/PRIORITY/RST_STREAM/SETTINGS/PUSH_PROMISE/
// PING/GOAWAY/WINDOW_UPDATE/CONTINUATION), stream lifecycle, connection
// and stream flow control, HPACK request decoding, response encoding,
// server push, and both h2c entry modes (prior knowledge and the HTTP/1.1
// Upgrade dance). Runs cooperatively from WebConnection.Tick().
//
// Style note: JIT-compiled by the Tier-0 JIT - plain arrays and loops,
// no BCL collections, no address-of on class fields.

using System;
using NeutrinoOS.DDK.Kernel;

namespace NeutrinoOS.DDK.Services;

/// <summary>HTTP/2 frame types (RFC 9113 section 6).</summary>
public static class H2Frame
{
    public const byte Data = 0;
    public const byte Headers = 1;
    public const byte Priority = 2;
    public const byte RstStream = 3;
    public const byte Settings = 4;
    public const byte PushPromise = 5;
    public const byte Ping = 6;
    public const byte GoAway = 7;
    public const byte WindowUpdate = 8;
    public const byte Continuation = 9;
}

/// <summary>HTTP/2 settings identifiers.</summary>
public static class H2Setting
{
    public const ushort HeaderTableSize = 1;
    public const ushort EnablePush = 2;
    public const ushort MaxConcurrentStreams = 3;
    public const ushort InitialWindowSize = 4;
    public const ushort MaxFrameSize = 5;
    public const ushort MaxHeaderListSize = 6;
}

/// <summary>Known HTTP/2 error codes (RFC 9113 section 7).</summary>
public static class H2Error
{
    public const uint NoError = 0;
    public const uint ProtocolError = 1;
    public const uint InternalError = 2;
    public const uint FlowControlError = 3;
    public const uint StreamClosed = 5;
    public const uint FrameSizeError = 6;
    public const uint RefusedStream = 7;
    public const uint Cancel = 8;
    public const uint CompressionError = 9;
}

/// <summary>One HTTP/2 stream.</summary>
public sealed class Http2Stream
{
    public int Id;
    public bool Open;               // still counted against concurrency
    public bool EndStreamReceived;  // request fully received
    public long SendWindow;         // remaining bytes we may send
    public byte[] Pending;          // DATA not yet writable (flow control)
    public int PendingOffset;
    public int PendingLength;

    // Request assembly: HEADERS records method/path, DATA frames fill
    // ReqBody, and the request is served once END_STREAM arrives.
    public string Method;
    public string Path;
    public bool Responded;
    public byte[] ReqBody;          // reused across requests on this slot
    public int ReqBodyLen;
    public bool ReqBodyOverflow;    // body exceeded MaxReqBody -> 413
    public long RecvWindow;         // our advertised receive window left
}

/// <summary>
/// An HTTP/2 server connection over a byte-oriented transport supplied by
/// the owning WebConnection (plain TCP or TLS).
/// </summary>
public sealed class Http2Connection
{
    private const int MaxStreams = 16;
    private const int DefaultFrameSize = 16384;
    // Per-request body cap. Deliberately below GCHeap.LOHThreshold (85000):
    // every allocation of 85 KB or more lands on the Large Object Heap, and
    // the LOH free list has been observed to hand out blocks that overlap
    // live objects (same failure the normal heap's free list was disabled
    // for). A 32 KB body keeps both the byte buffer and the char[] used to
    // convert it well inside the normal heap; larger requests get 413.
    private const int MaxReqBody = 32768;
    private const int InitialReqBody = 16384;
    private const long InitialRecvWindow = 65535;

    private readonly WebConnection _owner;
    private readonly HpackDecoder _hpack = new HpackDecoder();
    private readonly byte[] _in = new byte[16384 + 64];
    private int _inLen;
    private readonly byte[] _frame = new byte[9];
    private readonly byte[] _scratch = new byte[65536];

    private readonly long[] _streamIds = new long[MaxStreams];
    private readonly Http2Stream[] _streams = new Http2Stream[MaxStreams];

    private long _connSendWindow = 65535;   // updated by peer WINDOW_UPDATE
    private long _connRecvWindow = 65535;
    private int _peerMaxFrame = DefaultFrameSize;
    private int _peerInitialWindow = 65535;
    private bool _peerEnablePush;
    private bool _settingsSent;
    private bool _closed;
    private int _lastStreamId;              // highest client stream seen
    private int _nextPushId = 2;            // server push stream ids (even)
    private int _continuationStream;
    private readonly byte[] _headerBlock = new byte[32768];
    private int _headerBlockLen;
    private byte _continuationFlags;

    // h2c Upgrade: the original HTTP/1.1 request becomes stream 1.
    private bool _upgradeMode;
    private string _upgradeMethod;
    private string _upgradePath;
    private string _upgradeBody;

    /// <summary>The magic connection preface: PRI * HTTP/2.0 CRLF CRLF SM CRLF CRLF (24 octets).</summary>
    public static readonly byte[] ClientPreface = new byte[]
    {
        (byte)'P', (byte)'R', (byte)'I', (byte)' ', (byte)'*', (byte)' ',
        (byte)'H', (byte)'T', (byte)'T', (byte)'P', (byte)'/', (byte)'2',
        (byte)'.', (byte)'0', 13, 10, 13, 10, (byte)'S', (byte)'M', 13, 10,
        13, 10,
    };

    /// <summary>True once any stream has been served (used by WebConnection).</summary>
    public bool ServedRequest { get; private set; }

    /// <summary>
    /// True while any stream still needs the transport (response awaiting
    /// flow-control credit). Used by the owner to decide whether a peer
    /// close can tear the connection down immediately.
    /// </summary>
    public bool HasOpenWork()
    {
        for (int i = 0; i < MaxStreams; i++)
        {
            if (_streams[i].Open)
                return true;
        }
        return false;
    }

    /// <summary>True when the connection finished/errored.</summary>
    public bool Closed => _closed;

    public Http2Connection(WebConnection owner)
    {
        _owner = owner;
        for (int i = 0; i < MaxStreams; i++)
            _streams[i] = new Http2Stream();
    }

    /// <summary>Enable the h2c upgrade flow: HTTP/1.1 request served as stream 1.</summary>
    public void BeginUpgrade(string method, string path, string reqBody, byte[] settingsPayload)
    {
        _upgradeMode = true;
        _upgradeMethod = method;
        _upgradePath = path;
        _upgradeBody = reqBody;
        if (settingsPayload != null)
            ApplySettings(settingsPayload, settingsPayload.Length);
    }

    // ====================================================================
    // Transport helpers
    // ====================================================================

    private int Read(byte[] buf, int off, int len) => _owner.TransportRead(buf, off, len);

    private bool Write(byte[] buf, int off, int len) => _owner.TransportWrite(buf, off, len);

    private void SendFrame(byte type, byte flags, int streamId, byte[] payload, int payloadLen)
    {
        if (_closed)
            return;
        var hdr = new byte[9];
        hdr[0] = (byte)(payloadLen >> 16);
        hdr[1] = (byte)(payloadLen >> 8);
        hdr[2] = (byte)payloadLen;
        hdr[3] = type;
        hdr[4] = flags;
        hdr[5] = (byte)(streamId >> 24);
        hdr[6] = (byte)(streamId >> 16);
        hdr[7] = (byte)(streamId >> 8);
        hdr[8] = (byte)streamId;
        if (!Write(hdr, 0, 9))
        {
            _closed = true;
            return;
        }
        if (payloadLen > 0 && !Write(payload, 0, payloadLen))
            _closed = true;
    }

    private void SendSettings()
    {
        var s = new byte[18];
        int n = 0;
        n = PutSetting(s, n, H2Setting.MaxConcurrentStreams, MaxStreams);
        n = PutSetting(s, n, H2Setting.InitialWindowSize, 65535);
        n = PutSetting(s, n, H2Setting.MaxFrameSize, DefaultFrameSize);
        SendFrame(H2Frame.Settings, 0, 0, s, n);
        _settingsSent = true;
    }

    private static int PutSetting(byte[] b, int n, ushort id, int value)
    {
        b[n++] = (byte)(id >> 8);
        b[n++] = (byte)id;
        b[n++] = (byte)(value >> 24);
        b[n++] = (byte)(value >> 16);
        b[n++] = (byte)(value >> 8);
        b[n++] = (byte)value;
        return n;
    }

    private void SendGoAway(uint error)
    {
        var p = new byte[8];
        p[0] = (byte)(_lastStreamId >> 24);
        p[1] = (byte)(_lastStreamId >> 16);
        p[2] = (byte)(_lastStreamId >> 8);
        p[3] = (byte)_lastStreamId;
        p[4] = (byte)(error >> 24);
        p[5] = (byte)(error >> 16);
        p[6] = (byte)(error >> 8);
        p[7] = (byte)error;
        SendFrame(H2Frame.GoAway, 0, 0, p, 8);
    }

    private void Fail(uint error)
    {
        SendGoAway(error);
        _closed = true;
    }

    // ====================================================================
    // Main loop
    // ====================================================================

    /// <summary>Seed bytes already buffered by the owner (before handover).</summary>
    public void Seed(byte[] buf, int off, int len)
    {
        for (int i = 0; i < len && _inLen < _in.Length; i++)
            _in[_inLen++] = buf[off + i];
    }

    private bool _magicDone;

    /// <summary>One cooperative work slice.</summary>
    public void Tick()
    {
        if (_closed)
            return;

        // Connection handshake: consume the client magic preface, then
        // send our SETTINGS. In h2c-upgrade mode the 101 was already
        // written by WebConnection (before our SETTINGS, per RFC 7540
        // 3.2); stream 1 may be answered as soon as the magic arrives.
        if (!_magicDone)
        {
            int got = Fill();
            if (got < 0)
            {
                _closed = true;
                return;
            }
            if (_inLen < ClientPreface.Length)
                return;
            for (int i = 0; i < ClientPreface.Length; i++)
            {
                if (_in[i] != ClientPreface[i])
                {
                    Fail(H2Error.ProtocolError);
                    return;
                }
            }
            Consume(ClientPreface.Length);
            _magicDone = true;
            SendSettings();
            if (_upgradeMode)
            {
                ServeStream1();
            }
        }

        // Process every complete buffered frame first (the preface and the
        // client's first frames often arrive in one segment), then read
        // more; stop when the transport has nothing further buffered.
        while (!_closed)
        {
            while (!_closed && _inLen >= 9)
            {
                int len = (_in[0] << 16) | (_in[1] << 8) | _in[2];
                byte type = _in[3];
                byte flags = _in[4];
                int streamId = ((_in[5] & 0x7F) << 24) | (_in[6] << 16) | (_in[7] << 8) | _in[8];
                if (len > 16384)
                {
                    Fail(H2Error.FrameSizeError);
                    return;
                }
                if (_inLen < 9 + len)
                    break;
                for (int i = 0; i < len; i++)
                    _scratch[i] = _in[9 + i];
                Consume(9 + len);
                ProcessFrame(type, flags, streamId, _scratch, len);
            }
            int n = Fill();
            if (n < 0)
            {
                _closed = true;
                break;
            }
            if (n == 0)
                break;
        }
    }

    private int Fill()
    {
        if (_inLen >= _in.Length)
            return 0;
        int got = Read(_in, _inLen, _in.Length - _inLen);
        if (got > 0)
            _inLen += got;
        return got;
    }

    private void Consume(int count)
    {
        for (int i = count; i < _inLen; i++)
            _in[i - count] = _in[i];
        _inLen -= count;
    }

    // ====================================================================
    // Frame processing
    // ====================================================================

    private void ProcessFrame(byte type, byte flags, int streamId, byte[] payload, int len)
    {
        switch (type)
        {
            case H2Frame.Settings:
                if (streamId != 0)
                {
                    Fail(H2Error.ProtocolError);
                    return;
                }
                if ((flags & 0x1) != 0)
                    return;   // our SETTINGS ack
                if ((len % 6) != 0)
                {
                    Fail(H2Error.FrameSizeError);
                    return;
                }
                ApplySettings(payload, len);
                SendFrame(H2Frame.Settings, 0x1, 0, null, 0);   // ack
                return;

            case H2Frame.Ping:
                if (streamId != 0 || len != 8)
                {
                    Fail(H2Error.ProtocolError);
                    return;
                }
                if ((flags & 0x1) == 0)
                    SendFrame(H2Frame.Ping, 0x1, 0, payload, 8);
                return;

            case H2Frame.Headers:
                if (streamId == 0 || streamId % 2 == 0)
                {
                    Fail(H2Error.ProtocolError);
                    return;
                }
                _lastStreamId = streamId < _lastStreamId ? _lastStreamId : streamId;
                // Strip optional PADDED + PRIORITY fields.
                int start = 0;
                if ((flags & 0x8) != 0)
                {
                    if (len < 1)
                    {
                        Fail(H2Error.ProtocolError);
                        return;
                    }
                    int pad = payload[0];
                    if (len < 1 + pad)
                    {
                        Fail(H2Error.ProtocolError);
                        return;
                    }
                    len -= 1 + pad;
                    start = 1;
                }
                if ((flags & 0x20) != 0)
                {
                    if (len < 5)
                    {
                        Fail(H2Error.ProtocolError);
                        return;
                    }
                    len -= 5;
                    start += 5;
                }
                _headerBlockLen = 0;
                _continuationStream = streamId;
                _continuationFlags = flags;
                AppendHeaderBlock(payload, start, len);
                if ((flags & 0x4) != 0)   // END_HEADERS: block complete
                    ProcessHeaderBlock(streamId);
                return;

            case H2Frame.Continuation:
                if (streamId != _continuationStream || _continuationStream == 0)
                {
                    Fail(H2Error.ProtocolError);
                    return;
                }
                AppendHeaderBlock(payload, 0, len);
                if ((flags & 0x4) != 0)
                    ProcessHeaderBlock(streamId);
                return;

            case H2Frame.Data:
                if (streamId == 0)
                {
                    Fail(H2Error.ProtocolError);
                    return;
                }
                HandleData(streamId, flags, payload, len);
                return;

            case H2Frame.WindowUpdate:
                if (len != 4)
                {
                    Fail(H2Error.FrameSizeError);
                    return;
                }
                int inc = ((payload[0] & 0x7F) << 24) | (payload[1] << 16) |
                          (payload[2] << 8) | payload[3];
                if (inc <= 0)
                {
                    Fail(H2Error.ProtocolError);
                    return;
                }
                if (streamId == 0)
                {
                    _connSendWindow += inc;
                    FlushPending();
                }
                else
                {
                    var st = FindStream(streamId);
                    if (st != null)
                    {
                        st.SendWindow += inc;
                        FlushStream(st);
                    }
                }
                return;

            case H2Frame.RstStream:
                if (len != 4)
                {
                    Fail(H2Error.FrameSizeError);
                    return;
                }
                CloseStream(streamId);
                return;

            case H2Frame.Priority:
                return;   // valid but unused

            case H2Frame.PushPromise:
                Fail(H2Error.ProtocolError);   // clients must not push
                return;

            case H2Frame.GoAway:
                _closed = true;
                return;

            default:
                return;   // unknown frame types are ignored (RFC 9113 4.1)
        }
    }

    private void ApplySettings(byte[] payload, int len)
    {
        int n = 0;
        while (n + 6 <= len)
        {
            ushort id = (ushort)((payload[n] << 8) | payload[n + 1]);
            int value = (payload[n + 2] << 24) | (payload[n + 3] << 16) |
                        (payload[n + 4] << 8) | payload[n + 5];
            n += 6;
            if (id == H2Setting.InitialWindowSize)
            {
                if (value > 0x7FFFFFFF)
                {
                    Fail(H2Error.FlowControlError);
                    return;
                }
                long delta = (uint)value - (uint)_peerInitialWindow;
                _peerInitialWindow = value;
                for (int i = 0; i < MaxStreams; i++)
                {
                    if (_streams[i].Open)
                        _streams[i].SendWindow += delta;
                }
            }
            else if (id == H2Setting.MaxFrameSize)
            {
                if (value < 16384 || value > 16777215)
                {
                    Fail(H2Error.ProtocolError);
                    return;
                }
                _peerMaxFrame = value;
            }
            else if (id == H2Setting.EnablePush)
            {
                _peerEnablePush = value != 0;
            }
            else if (id == H2Setting.HeaderTableSize)
            {
                // We never use dynamic-table encoding; nothing to resize.
            }
        }
    }

    private void AppendHeaderBlock(byte[] payload, int off, int len)
    {
        for (int i = 0; i < len && _headerBlockLen < _headerBlock.Length; i++)
            _headerBlock[_headerBlockLen++] = payload[off + i];
    }

    private void ProcessHeaderBlock(int streamId)
    {
        var names = new string[32];
        var values = new string[32];
        int count = _hpack.Decode(_headerBlock, _headerBlockLen, names, values);
        _headerBlockLen = 0;
        _continuationStream = 0;
        if (count < 0)
        {
            Fail(H2Error.CompressionError);
            return;
        }

        string method = null;
        string path = null;
        for (int i = 0; i < count; i++)
        {
            if (Hpack.StrEq(names[i], ":method"))
                method = values[i];
            else if (Hpack.StrEq(names[i], ":path"))
                path = values[i];
        }
        if (method == null || path == null)
        {
            ResetStream(streamId, H2Error.ProtocolError);
            return;
        }

        var st = GetOrCreateStream(streamId);
        if (st == null)
        {
            ResetStream(streamId, H2Error.RefusedStream);
            return;
        }
        st.Method = method;
        st.Path = path;
        st.EndStreamReceived = (_continuationFlags & 0x1) != 0;
        // Serve now when the request ended on HEADERS; otherwise wait for
        // the DATA frames (HandleData serves at END_STREAM).
        if (st.EndStreamReceived)
            ServeBufferedRequest(streamId, st);
    }

    private void HandleData(int streamId, byte flags, byte[] payload, int len)
    {
        var st = FindStream(streamId);
        if (st == null)
        {
            // Connection-level flow control still applies to discarded data.
            _connRecvWindow -= len;
            TopUpRecvWindow(len, 0);
            return;
        }
        _connRecvWindow -= len;
        st.RecvWindow -= len;
        TopUpRecvWindow(len, 0);
        TopUpStreamRecvWindow(st, len);

        if (st.Responded)
        {
            // Already answered (e.g. overflow); keep draining the stream.
            if ((flags & 0x1) != 0)
                st.EndStreamReceived = true;
            return;
        }

        // Buffer the request body for the deferred serve. The buffer starts
        // small and doubles on demand, never exceeding MaxReqBody; anything
        // larger is answered with 413 once the stream ends.
        if (len > 0)
        {
            if (st.ReqBody == null)
                st.ReqBody = new byte[InitialReqBody];
            if (st.ReqBodyLen + len > st.ReqBody.Length)
            {
                int want = st.ReqBodyLen + len;
                if (want > MaxReqBody)
                    want = MaxReqBody;
                int cap = st.ReqBody.Length;
                while (cap < want)
                    cap = cap * 2 > MaxReqBody ? MaxReqBody : cap * 2;
                if (cap > st.ReqBody.Length)
                {
                    var bigger = new byte[cap];
                    for (int i = 0; i < st.ReqBodyLen; i++)
                        bigger[i] = st.ReqBody[i];
                    st.ReqBody = bigger;
                }
            }
            int room = st.ReqBody.Length - st.ReqBodyLen;
            int take = len <= room ? len : room;
            for (int i = 0; i < take; i++)
                st.ReqBody[st.ReqBodyLen + i] = payload[i];
            st.ReqBodyLen += take;
            if (take < len)
                st.ReqBodyOverflow = true;
        }

        if ((flags & 0x1) != 0)
        {
            st.EndStreamReceived = true;
            ServeBufferedRequest(streamId, st);
        }
    }

    private void TopUpRecvWindow(int consumed, int streamId)
    {
        if (consumed <= 0)
            return;
        if (_connRecvWindow < 32768)
        {
            var wu = new byte[4];
            wu[0] = (byte)(consumed >> 24);
            wu[1] = (byte)(consumed >> 16);
            wu[2] = (byte)(consumed >> 8);
            wu[3] = (byte)consumed;
            SendFrame(H2Frame.WindowUpdate, 0, 0, wu, 4);
            _connRecvWindow += consumed;
        }
        _ = streamId;
    }

    /// <summary>Extend the stream-level receive window as DATA is consumed.</summary>
    private void TopUpStreamRecvWindow(Http2Stream st, int consumed)
    {
        if (consumed <= 0 || st.RecvWindow >= 32768)
            return;
        int streamId = 0;
        for (int i = 0; i < MaxStreams; i++)
        {
            if (_streams[i] == st)
            {
                streamId = (int)_streamIds[i];
                break;
            }
        }
        if (streamId == 0)
            return;
        var wu = new byte[4];
        wu[0] = (byte)(consumed >> 24);
        wu[1] = (byte)(consumed >> 16);
        wu[2] = (byte)(consumed >> 8);
        wu[3] = (byte)consumed;
        SendFrame(H2Frame.WindowUpdate, 0, streamId, wu, 4);
        st.RecvWindow += consumed;
    }

    // ====================================================================
    // Stream table
    // ====================================================================

    private Http2Stream FindStream(int id)
    {
        for (int i = 0; i < MaxStreams; i++)
        {
            if (_streams[i].Open && _streamIds[i] == id)
                return _streams[i];
        }
        return null;
    }

    private Http2Stream GetOrCreateStream(int id)
    {
        var existing = FindStream(id);
        if (existing != null)
            return existing;
        for (int i = 0; i < MaxStreams; i++)
        {
            if (!_streams[i].Open)
            {
                _streams[i].Open = true;
                _streams[i].EndStreamReceived = false;
                _streams[i].SendWindow = _peerInitialWindow;
                _streams[i].Pending = null;
                _streams[i].PendingOffset = 0;
                _streams[i].PendingLength = 0;
                _streams[i].Method = null;
                _streams[i].Path = null;
                _streams[i].Responded = false;
                // ReqBody is deliberately kept for reuse on this slot.
                _streams[i].ReqBodyLen = 0;
                _streams[i].ReqBodyOverflow = false;
                _streams[i].RecvWindow = InitialRecvWindow;
                _streamIds[i] = id;
                return _streams[i];
            }
        }
        return null;
    }

    private void CloseStream(int id)
    {
        for (int i = 0; i < MaxStreams; i++)
        {
            if (_streams[i].Open && _streamIds[i] == id)
            {
                _streams[i].Open = false;
                _streams[i].Pending = null;
                _streams[i].PendingOffset = 0;
                _streams[i].PendingLength = 0;
                return;
            }
        }
    }

    private void ResetStream(int id, uint error)
    {
        var st = FindStream(id);
        if (st != null)
            CloseStream(id);
        var p = new byte[4];
        p[0] = (byte)(error >> 24);
        p[1] = (byte)(error >> 16);
        p[2] = (byte)(error >> 8);
        p[3] = (byte)error;
        SendFrame(H2Frame.RstStream, 0, id, p, 4);
    }

    // ====================================================================
    // Request serving
    // ====================================================================

    private void ServeStream1()
    {
        // h2c upgrade: the captured HTTP/1.1 request is stream 1.
        var st = GetOrCreateStream(1);
        st.SendWindow = _peerInitialWindow;
        st.EndStreamReceived = true;
        st.Method = _upgradeMethod;
        st.Path = _upgradePath;
        ServeRequest(1, _upgradeMethod, _upgradePath, _upgradeBody ?? "", false, true);
    }

    /// <summary>
    /// Answer a request whose END_STREAM has been seen, using the DATA frames
    /// buffered on the stream as the request body.
    /// </summary>
    private void ServeBufferedRequest(int streamId, Http2Stream st)
    {
        if (st.Responded)
            return;
        st.Responded = true;
        string reqBody = "";
        if (!st.ReqBodyOverflow && st.ReqBodyLen > 0)
        {
            var chars = new char[st.ReqBodyLen];
            for (int i = 0; i < st.ReqBodyLen; i++)
                chars[i] = (char)st.ReqBody[i];
            reqBody = new string(chars);
        }
        ServeRequest(streamId, st.Method ?? "GET", st.Path ?? "/", reqBody, st.ReqBodyOverflow, true);
    }

    private void ServeRequest(int streamId, string method, string path, string reqBody,
        bool payloadTooLarge, bool sendInitialHeaders)
    {
        ServedRequest = true;

        int status;
        string statusText;
        string contentType;
        string body;
        if (!WebService.BuildRoute(method, path, reqBody, out status, out statusText, out contentType, out body))
        {
            status = 404;
            statusText = "Not Found";
            contentType = "text/plain";
            body = "404 not found\n";
        }

        bool headOnly = Hpack.StrEq(method, "HEAD");
        bool isGet = Hpack.StrEq(method, "GET");
        bool isPost = Hpack.StrEq(method, "POST");
        bool isPut = Hpack.StrEq(method, "PUT");
        bool isDelete = Hpack.StrEq(method, "DELETE");
        if (!isGet && !isPost && !isPut && !isDelete && !headOnly)
        {
            status = 405;
            statusText = "Method Not Allowed";
            contentType = "text/plain";
            body = "method not allowed\n";
        }
        if (payloadTooLarge)
        {
            status = 413;
            statusText = "Payload Too Large";
            contentType = "text/plain";
            body = "payload too large\n";
        }

        // Phase 7 parity: per-source-IP rate limiting.
        if (!WebService.AllowRequest(_owner.PeerIp))
        {
            status = 429;
            statusText = "Too Many Requests";
            contentType = "text/plain";
            body = "rate limit exceeded\n";
        }

        byte[] payload = null;
        int bodyLen = 0;
        if (!headOnly && body != null && body.Length > 0)
            payload = StringToUtf8(body, out bodyLen);

        if (sendInitialHeaders)
            SendStreamHeaders(streamId, status, statusText, contentType, bodyLen);

        var st = FindStream(streamId);
        if (st == null)
            return;

        if (bodyLen == 0)
        {
            SendFrame(H2Frame.Data, 0x1, streamId, null, 0);   // END_STREAM
            CloseStream(streamId);
            LogAccess(status, 0);
            return;
        }

        SendData(streamId, st, payload, bodyLen, true);
        LogAccess(status, bodyLen);

        // Server push: advertise a commonly needed sibling resource
        // (only when the peer allows pushes; curl sends ENABLE_PUSH=0).
        if (_peerEnablePush && (Hpack.StrEq(path, "/") || Hpack.StrEq(path, "/index.html")))
        {
            PushResource(streamId, _nextPushId, "/health");
            _nextPushId += 2;
        }
    }

    private void SendStreamHeaders(int streamId, int status, string statusText, string contentType, int contentLength)
    {
        var block = new byte[512];
        int n = 0;
        n = HpackEncoder.WriteHeader(block, n, ":status", WebService.IntToStr(status));
        if (contentLength >= 0)
        {
            n = HpackEncoder.WriteHeader(block, n, "content-type", contentType);
            n = HpackEncoder.WriteHeader(block, n, "content-length", WebService.IntToStr(contentLength));
        }
        n = HpackEncoder.WriteHeader(block, n, "server", "NeutrinoOS");
        SendFrame(H2Frame.Headers, 0x4, streamId, block, n);   // END_HEADERS
        _ = statusText;
    }

    private void SendData(int streamId, Http2Stream st, byte[] data, int length, bool endStream)
    {
        int off = 0;
        while (off < length)
        {
            int chunk = length - off;
            if (chunk > _peerMaxFrame)
                chunk = _peerMaxFrame;
            long available = _connSendWindow < st.SendWindow ? _connSendWindow : st.SendWindow;
            if (available <= 0)
                break;
            if (chunk > available)
                chunk = (int)available;
            bool last = off + chunk >= length;
            SendFrame(H2Frame.Data, last && endStream ? (byte)0x1 : (byte)0, streamId, Slice(data, off, chunk), chunk);
            _connSendWindow -= chunk;
            st.SendWindow -= chunk;
            off += chunk;
            if (last && endStream)
            {
                CloseStream(streamId);
                return;
            }
        }
        if (off < length)
        {
            // Flow control blocked: park the remainder.
            st.Pending = Slice(data, off, length - off);
            st.PendingOffset = 0;
            st.PendingLength = length - off;
        }
        else if (endStream)
        {
            SendFrame(H2Frame.Data, 0x1, streamId, null, 0);
            CloseStream(streamId);
        }
    }

    private void FlushPending()
    {
        for (int i = 0; i < MaxStreams; i++)
        {
            if (_streams[i].Open && _streams[i].PendingLength > 0)
                FlushStream(_streams[i]);
        }
    }

    private void FlushStream(Http2Stream st)
    {
        if (st.PendingLength <= 0)
            return;
        int streamId = 0;
        for (int i = 0; i < MaxStreams; i++)
        {
            if (_streams[i] == st)
            {
                streamId = (int)_streamIds[i];
                break;
            }
        }
        if (streamId == 0)
            return;

        var data = st.Pending;
        int off = st.PendingOffset;
        int end = st.PendingLength;
        while (off < end)
        {
            int chunk = end - off;
            if (chunk > _peerMaxFrame)
                chunk = _peerMaxFrame;
            long available = _connSendWindow < st.SendWindow ? _connSendWindow : st.SendWindow;
            if (available <= 0)
                break;
            if (chunk > available)
                chunk = (int)available;
            bool last = off + chunk >= end;
            SendFrame(H2Frame.Data, last ? (byte)0x1 : (byte)0, streamId, Slice(data, off, chunk), chunk);
            _connSendWindow -= chunk;
            st.SendWindow -= chunk;
            off += chunk;
            if (last)
            {
                st.Pending = null;
                st.PendingOffset = 0;
                st.PendingLength = 0;
                CloseStream(streamId);
                return;
            }
        }
        st.PendingOffset = off;
    }

    private static byte[] Slice(byte[] data, int off, int len)
    {
        if (off == 0 && len == data.Length)
            return data;
        var r = new byte[len];
        for (int i = 0; i < len; i++)
            r[i] = data[off + i];
        return r;
    }

    private static byte[] StringToUtf8(string s, out int length)
    {
        var b = new byte[s.Length * 3];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x80)
                b[n++] = (byte)c;
            else if (c < 0x800)
            {
                b[n++] = (byte)(0xC0 | (c >> 6));
                b[n++] = (byte)(0x80 | (c & 0x3F));
            }
            else
            {
                b[n++] = (byte)(0xE0 | (c >> 12));
                b[n++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                b[n++] = (byte)(0x80 | (c & 0x3F));
            }
        }
        length = n;
        var r = new byte[n];
        for (int i = 0; i < n; i++)
            r[i] = b[i];
        return r;
    }

    private static void LogAccess(int status, int bodyLen)
    {
        Debug.Write("[web] ");
        Debug.WriteDecimal(status);
        Debug.Write(" ");
        Debug.WriteDecimal(bodyLen);
        Debug.WriteLine("B (h2)");
    }

    /// <summary>Server push of one resource onto <paramref name="streamId"/>.</summary>
    public void PushResource(int streamId, int promisedId, string path)
    {
        if (!_peerEnablePush)
            return;
        int status;
        string statusText;
        string contentType;
        string body;
        if (!WebService.BuildRoute("GET", path, "", out status, out statusText, out contentType, out body))
            return;

        var block = new byte[512];
        int n = 0;
        n = HpackEncoder.WriteHeader(block, n, ":method", "GET");
        n = HpackEncoder.WriteHeader(block, n, ":path", path);
        n = HpackEncoder.WriteHeader(block, n, ":scheme", "http");
        n = HpackEncoder.WriteHeader(block, n, ":authority", "localhost");

        var payload = new byte[4 + n];
        payload[0] = (byte)(promisedId >> 24);
        payload[1] = (byte)(promisedId >> 16);
        payload[2] = (byte)(promisedId >> 8);
        payload[3] = (byte)promisedId;
        for (int i = 0; i < n; i++)
            payload[4 + i] = block[i];
        SendFrame(H2Frame.PushPromise, 0x4, streamId, payload, payload.Length);

        var st = GetOrCreateStream(promisedId);
        if (st == null)
            return;
        var bodyBytes = StringToUtf8(body, out int bodyLen);
        SendStreamHeaders(promisedId, status, statusText, contentType, bodyLen);
        SendData(promisedId, st, bodyBytes, bodyLen, true);
        _ = streamId;
    }
}
