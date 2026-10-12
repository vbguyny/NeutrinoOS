// NeutrinoOS Web - Kestrel port milestone M2: the host engine on the
// socket connection transport.
//
// WebHost owns a SocketConnectionListener plus a small pool of connection
// slots and drives them from the cooperative service Tick() (kernel idle
// hook): no threads, no async yet. Requests are read from each connection's
// input pipe (socket -> app) and responses are written to its output pipe
// (app -> socket); the transport pumps move bytes on the same Tick.
//
// HTTP/1.1 keep-alive: persistent by default; a request with
// `Connection: close` (or HTTP/1.0, or a protocol error, or the
// per-connection request cap) closes the connection after the response.
// Pipelined requests in the same read are served in order.
//
// See docs/KESTREL-PORT.md for the milestone plan (M3 replaces the
// hand-rolled head parse with the Kestrel HttpParser shape).

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NeutrinoOS.DDK.Connections;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Stack;
using NeutrinoOS.DDK.Services;   // WebService's proven string helpers (same assembly)

namespace NeutrinoOS.DDK.Web;

/// <summary>The HTTP/1.1 host engine on the socket connection transport.</summary>
public sealed class WebHost
{
    private const int MaxConnections = 4;
    private const int MaxRequestBytes = 4096;   // head + body must fit
    private const int MaxRoutes = 32;
    private const int MaxPathSegments = 8;      // per pattern/path
    private const int MaxRequestsPerConnection = 100;
    private const ulong IdleTimeoutMs = 10000;

    /// <summary>Log prefix, e.g. "webapi" (prints as [webapi] ...).</summary>
    public string Name;

    /// <summary>TCP port to bind (default set by WebApplication: 8080).</summary>
    public ushort Port;

    private SocketConnectionListener _listener;
    private NetworkStack _stack;
    private readonly Connection[] _connections = new Connection[MaxConnections];
    private bool _active;
    private ulong _startedAt;
    private int _requests;

    private readonly string[] _routeMethods = new string[MaxRoutes];
    private readonly string[] _routePatterns = new string[MaxRoutes];
    private readonly RequestHandler[] _routeHandlers = new RequestHandler[MaxRoutes];
    private readonly RequestHandlerAsync[] _routeAsyncHandlers = new RequestHandlerAsync[MaxRoutes];
    private int _routeCount;

    /// <summary>Creates a host; call Start()/Tick()/Stop() like a service.</summary>
    public WebHost(string name, ushort port)
    {
        Name = name;
        Port = port;
    }

    /// <summary>True while the listener is running.</summary>
    public bool IsRunning => _active;

    /// <summary>Requests handled since the last Start().</summary>
    public int RequestCount => _requests;

    /// <summary>Register a handler for a method + pattern ("/x/{id}").</summary>
    public void AddRoute(string method, string pattern, RequestHandler handler)
    {
        if (_routeCount >= MaxRoutes)
            return;
        _routeMethods[_routeCount] = method;
        _routePatterns[_routeCount] = pattern;
        _routeHandlers[_routeCount] = handler;
        _routeCount++;
    }

    /// <summary>Register an async handler for a method + pattern.</summary>
    public void AddRouteAsync(string method, string pattern, RequestHandlerAsync handler)
    {
        if (_routeCount >= MaxRoutes)
            return;
        _routeMethods[_routeCount] = method;
        _routePatterns[_routeCount] = pattern;
        _routeAsyncHandlers[_routeCount] = handler;
        _routeCount++;
    }

    // ====================================================================
    // Service contract (driven by the kernel idle hook)
    // ====================================================================

    /// <summary>Bind and listen; 0 = ok (-30 no eth0, -32 bind failed).</summary>
    public int Start()
    {
        if (_active)
            return 0;

        var eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.WriteLine("[" + Name + "] no eth0 interface - start QEMU with a NIC");
            return -30;
        }
        _stack = eth.Stack;

        _listener = new SocketConnectionListener(_stack, new ConnectionEndPoint(Port));
        if (!_listener.Start())
        {
            _listener = null;
            Console.WriteLine("[" + Name + "] bind failed on port " + WebService.IntToStr(Port));
            return -32;
        }

        for (int i = 0; i < _connections.Length; i++)
            _connections[i] = null;
        _requests = 0;
        _startedAt = Timer.GetUptimeMilliseconds();
        _active = true;
        Console.WriteLine("[" + Name + "] listening on port " + WebService.IntToStr(Port) +
            " - try GET /api/v1/info");
        return 0;
    }

    /// <summary>Stop the listener and drop all connections.</summary>
    public void Stop()
    {
        if (!_active)
            return;
        for (int i = 0; i < _connections.Length; i++)
        {
            var conn = _connections[i];
            if (conn != null)
            {
                conn.Context.Abort();
                _connections[i] = null;
            }
        }
        if (_listener != null)
        {
            _listener.UnbindAsync();
            _listener.DisposeAsync();
            _listener = null;
        }
        _active = false;
        Console.WriteLine("[" + Name + "] stopped");
    }

    /// <summary>One bounded work slice (called from the shell idle hook).</summary>
    public void Tick()
    {
        if (!_active)
            return;

        // Accept new connections (stack pump + pending accepts).
        _listener.Tick();
        while (_listener.TryAccept(out SocketConnectionContext? accepted))
        {
            if (accepted == null)
                break;
            int slot = -1;
            for (int i = 0; i < MaxConnections; i++)
            {
                if (_connections[i] == null)
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0)
            {
                accepted.Abort();   // pool full: drop
                continue;
            }
            _connections[slot] = new Connection(accepted);
        }

        // Service each connection: pump the transport, then HTTP.
        ulong now = Timer.GetUptimeMilliseconds();
        for (int i = 0; i < MaxConnections; i++)
        {
            var conn = _connections[i];
            if (conn == null)
                continue;
            ServiceConnection(conn, now);

            // A closing connection keeps pumping until the transport is
            // finished (response drained + FIN), with a bounded grace.
            if (!conn.Released && conn.Done)
            {
                conn.GraceTicks++;
                if (conn.Context.IsFinished || conn.GraceTicks > 400)
                    conn.Released = true;
            }
            if (conn.Released)
            {
                conn.Context.Abort();
                _connections[i] = null;
            }
        }
    }

    // ====================================================================
    // Connection handling
    // ====================================================================

    /// <summary>One connection slot: transport + request buffer + state.</summary>
    private sealed class Connection
    {
        public readonly SocketConnectionContext Context;
        public readonly byte[] Buffer = new byte[MaxRequestBytes];
        public readonly char[] BodyChars = new char[MaxRequestBytes];
        public int Length;
        public ulong LastActivity;
        public bool Done;        // response written and closing; pump out, then release
        public bool Released;    // context finished; slot can be reused
        public bool InputClosed; // peer EOF seen
        public int Requests;     // served on this connection (keep-alive cap)
        public int GraceTicks;
        public bool Sent100;     // initial "100 Continue" sent for the current request
        public Task PendingTask; // async handler still running (null = none)
        public HttpResponse PendingResponse;   // written when the task completes
        public bool PendingKeepAlive;

        public Connection(SocketConnectionContext context)
        {
            Context = context;
            LastActivity = Timer.GetUptimeMilliseconds();
        }
    }

    /// <summary>Send "100 Continue" once per request when the client asked.</summary>
    private static void SendContinueIfExpected(Connection conn, bool expected)
    {
        if (!expected || conn.Sent100)
            return;
        PipeWriter writer = conn.Context.OutputWriter;
        string interim = "HTTP/1.1 100 Continue\r\n\r\n";
        Span<byte> span = writer.GetSpan(interim.Length);
        for (int i = 0; i < interim.Length; i++)
            span[i] = (byte)interim[i];
        writer.Advance(interim.Length);
        writer.FlushAsync();
        conn.Sent100 = true;
    }

    private void ServiceConnection(Connection conn, ulong now)
    {
        if (!conn.Done && now - conn.LastActivity > IdleTimeoutMs)
        {
            conn.Released = true;
            return;
        }

        // Pump the transport (socket <-> pipes) first.
        conn.Context.Tick();
        if (conn.Done)
            return;   // keep pumping above until IsFinished / grace expiry

        // Drain readable bytes from the input pipe into the request buffer.
        PipeReader reader = conn.Context.InputReader;
        while (conn.Length < conn.Buffer.Length && reader.TryRead(out ReadResult result))
        {
            ReadOnlySequence<byte> buffer = result.Buffer;
            long total = buffer.Length;
            if (!buffer.IsEmpty)
            {
                // First-memory copy (segment iteration is avoided on purpose
                // during M2 bring-up - see Pipe/korlib notes).
                ReadOnlyMemory<byte> mem = buffer.First;
                ReadOnlySpan<byte> src = mem.Span;
                int space = conn.Buffer.Length - conn.Length;
                int copy = src.Length < space ? src.Length : space;
                for (int i = 0; i < copy; i++)
                    conn.Buffer[conn.Length + i] = src[i];
                conn.Length += copy;
                if (copy > 0)
                    conn.LastActivity = now;
                if (copy == (int)total)
                    reader.AdvanceTo(buffer.End);
                else
                    reader.AdvanceTo(buffer.GetPosition(copy));
                if (result.IsCompleted)
                    conn.InputClosed = true;
                if (copy < src.Length)
                    break;      // request buffer full: handled below
            }
            else
            {
                if (result.IsCompleted)
                    conn.InputClosed = true;
                break;
            }
        }

        // Serve every complete request currently buffered (keep-alive loop).
        while (!conn.Done)
        {
            // Finish a deferred (async handler) response from an earlier tick.
            if (conn.PendingTask != null)
            {
                if (!conn.PendingTask.IsCompleted)
                    return;   // handler still running: stop for this tick
                HttpResponse pending = conn.PendingResponse;
                bool pendingKeepAlive = conn.PendingKeepAlive;
                conn.PendingTask = null;
                conn.PendingResponse = null;
                WriteResponse(conn, pending.StatusCode, pending.ContentType,
                    pending.Body, pendingKeepAlive);
                if (!pendingKeepAlive)
                {
                    conn.Done = true;
                    return;
                }
                if (conn.Length == 0)
                    return;   // nothing pipelined behind it
                // fall through: serve any pipelined request
            }

            string method;
            string target;
            int contentLength;
            int headEnd;
            int flags;
            int parsed = HttpParser.ParseRequestHead(conn.Buffer, conn.Length,
                out method, out target, out contentLength, out headEnd, out flags);
            if (parsed == HttpParser.ResultIncomplete)
            {
                if (conn.Length >= conn.Buffer.Length)
                    RespondAndFinish(conn, 431, "text/plain", "request too large\n");
                else if (conn.InputClosed)
                    conn.Released = true;   // EOF between/inside requests
                return;
            }
            if (parsed == HttpParser.ResultBad)
            {
                RespondAndFinish(conn, 400, "text/plain", "bad request\n");
                return;
            }

            bool http11 = (flags & HttpParser.FlagHttp11) != 0;
            bool connClose = (flags & HttpParser.FlagConnClose) != 0;
            bool chunked = (flags & HttpParser.FlagChunked) != 0;
            bool expectContinue = (flags & HttpParser.FlagExpectContinue) != 0;

            string reqBody = "";
            int total;   // bytes of this request currently in the buffer
            if (chunked)
            {
                int bodyLen;
                int consumed = HttpParser.TryDecodeChunked(conn.Buffer, headEnd,
                    conn.Length - headEnd, conn.BodyChars, out bodyLen);
                if (consumed == HttpParser.DecodeIncomplete)
                {
                    SendContinueIfExpected(conn, expectContinue);
                    if (conn.Length >= conn.Buffer.Length)
                    {
                        RespondAndFinish(conn, 413, "text/plain", "body too large\n");
                        return;
                    }
                    return;   // keep waiting for more chunks
                }
                if (consumed == HttpParser.DecodeMalformed)
                {
                    RespondAndFinish(conn, 400, "text/plain", "bad chunked body\n");
                    return;
                }
                if (consumed == HttpParser.DecodeTooLarge)
                {
                    RespondAndFinish(conn, 413, "text/plain", "body too large\n");
                    return;
                }
                total = headEnd + consumed;
                if (bodyLen > 0)
                    reqBody = HttpParser.StrOf(conn.BodyChars, bodyLen);
            }
            else
            {
                total = headEnd + contentLength;
                if (total > conn.Buffer.Length)
                {
                    RespondAndFinish(conn, 413, "text/plain", "body too large\n");
                    return;
                }
                if (conn.Length < total)
                {
                    SendContinueIfExpected(conn, expectContinue);
                    return;   // keep waiting for the rest of the body
                }
                if (contentLength > 0)
                    reqBody = HttpParser.CharsOf(conn.Buffer, headEnd, contentLength);
            }

            bool keepAlive = http11 && !connClose && conn.Requests < MaxRequestsPerConnection;
            Dispatch(conn, method, target, reqBody, keepAlive);
            conn.Sent100 = false;

            // Consume the request; keep any pipelined remainder.
            int left = conn.Length - total;
            for (int i = 0; i < left; i++)
                conn.Buffer[i] = conn.Buffer[total + i];
            conn.Length = left;
            conn.Requests++;
            conn.LastActivity = now;

            if (conn.PendingTask != null)
                return;   // async response deferred; resume at the loop top

            if (!keepAlive)
            {
                conn.Done = true;
                return;
            }
            if (conn.Length == 0)
                return;   // wait for the next request
        }
    }

    /// <summary>Route one complete request and write the response.</summary>
    private void Dispatch(Connection conn, string method, string target, string reqBody, bool keepAlive)
    {
        _requests++;

        // Split "path?query".
        string path = target;
        string query = "";
        int qm = WebService.IndexOfChar(target, '?');
        if (qm >= 0)
        {
            path = target.Substring(0, qm);
            query = target.Substring(qm + 1, target.Length - qm - 1);
        }

        var pseg = new string[MaxPathSegments];
        int pcount;
        SplitPath(path, pseg, out pcount);

        int matched = -1;
        bool pathMatched = false;
        string[] capNames = null;
        string[] capValues = null;
        int capCount = 0;

        for (int i = 0; i < _routeCount; i++)
        {
            var rseg = new string[MaxPathSegments];
            int rcount;
            SplitPath(_routePatterns[i], rseg, out rcount);
            if (rcount != pcount)
                continue;

            var names = new string[MaxPathSegments];
            var values = new string[MaxPathSegments];
            int ncapt = 0;
            bool ok = true;
            for (int s = 0; s < rcount; s++)
            {
                string rs = rseg[s];
                if (rs.Length >= 2 && rs[0] == '{' && rs[rs.Length - 1] == '}')
                {
                    names[ncapt] = rs.Substring(1, rs.Length - 2);
                    values[ncapt] = pseg[s];
                    ncapt++;
                }
                else if (!WebService.StrEq(rs, pseg[s]))
                {
                    ok = false;
                    break;
                }
            }
            if (!ok)
                continue;

            pathMatched = true;
            if (WebService.StrEq(_routeMethods[i], method))
            {
                matched = i;
                capNames = names;
                capValues = values;
                capCount = ncapt;
                break;
            }
        }

        if (matched < 0)
        {
            if (pathMatched)
                RespondAndFinish(conn, 405, "text/plain", "method not allowed\n");
            else
                RespondAndFinish(conn, 404, "application/json", "{\"error\":\"not found\"}\n");
            return;
        }

        var ctx = new HttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.QueryString = query;
        ctx.Request.Body = reqBody;
        ctx.Request.SetRoute(capNames, capValues, capCount);

        RequestHandler sync = _routeHandlers[matched];
        if (sync != null)
        {
            sync(ctx);
        }
        else
        {
            Task deferred = _routeAsyncHandlers[matched](ctx);
            if (!deferred.IsCompleted)
            {
                // The handler suspended: keep the response and let the serve
                // loop write it once the task completes (later ticks).
                conn.PendingTask = deferred;
                conn.PendingResponse = ctx.Response;
                conn.PendingKeepAlive = keepAlive;
                return;
            }
        }

        WriteResponse(conn, ctx.Response.StatusCode, ctx.Response.ContentType, ctx.Response.Body, keepAlive);
        if (!keepAlive)
            conn.Done = true;
    }

    // ====================================================================
    // HTTP I/O helpers (proven logic from SampleApi.cs; M2 writes through
    // the connection's output pipe instead of the raw socket)
    // ====================================================================

    /// <summary>Write a complete response and close the connection after it.</summary>
    private static void RespondAndFinish(Connection conn, int status, string contentType, string body)
    {
        WriteResponse(conn, status, contentType, body, false);
        conn.Done = true;
    }

    /// <summary>Queue a complete response on the connection's output pipe.</summary>
    private static void WriteResponse(Connection conn, int status, string contentType, string body, bool keepAlive)
    {
        if (contentType == null || contentType.Length == 0)
            contentType = "text/plain";
        if (body == null)
            body = "";
        string head =
            "HTTP/1.1 " + WebService.IntToStr(status) + " " + WebStatus.TextFor(status) + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + WebService.IntToStr(body.Length) + "\r\n" +
            "Server: NeutrinoOS\r\n" +
            "Connection: " + (keepAlive ? "keep-alive" : "close") + "\r\n\r\n";

        PipeWriter writer = conn.Context.OutputWriter;
        Span<byte> span = writer.GetSpan(head.Length + body.Length);
        for (int i = 0; i < head.Length; i++)
            span[i] = (byte)head[i];
        for (int i = 0; i < body.Length; i++)
            span[head.Length + i] = (byte)body[i];
        writer.Advance(head.Length + body.Length);
        writer.FlushAsync();

        if (!keepAlive)
        {
            // Completing signals EOF: the transport drains the response and
            // then shuts the send side (FIN).
            writer.Complete();
        }
    }

    /// <summary>Split "/a/b/{id}" into ["a","b","{id}"] ('/' alone = 0 segments).</summary>
    private static void SplitPath(string s, string[] segs, out int count)
    {
        count = 0;
        if (s == null || s.Length == 0)
            return;
        int i = 0;
        if (s[0] == '/')
            i = 1;
        if (i >= s.Length)
            return;   // "/" has no segments
        int start = i;
        while (i <= s.Length)
        {
            if (i == s.Length || s[i] == '/')
            {
                if (count >= MaxPathSegments)
                    return;   // too deep: caller treats it as no match
                segs[count] = s.Substring(start, i - start);
                count++;
                start = i + 1;
            }
            i++;
        }
    }
}
