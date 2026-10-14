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
    // Engine limits live in KestrelServerOptions (M4); these cached
    // fields are frozen at construction from the builder's options.
    private readonly int _maxConnections;
    private readonly int _maxRequestBytes;
    private readonly int _maxRoutes;
    private readonly int _maxPathSegments;
    private readonly int _maxRequestsPerConnection;
    private readonly ulong _idleTimeoutMs;

    /// <summary>The options this host was constructed with (M4).</summary>
    public readonly KestrelServerOptions Options;

    /// <summary>Log prefix, e.g. "webapi" (prints as [webapi] ...).</summary>
    public string Name;

    /// <summary>TCP port to bind (default set by WebApplication: 8080).</summary>
    public ushort Port;

    private SocketConnectionListener _listener;
    private SocketConnectionListener _httpsListener;
    private NetworkStack _stack;
    private readonly Connection[] _connections;
    private bool _active;
    private ulong _startedAt;
    private int _requests;

    private readonly string[] _routeMethods;
    private readonly string[] _routePatterns;
    private readonly RequestHandler[] _routeHandlers;
    private readonly RequestHandlerAsync[] _routeAsyncHandlers;
    private readonly int[] _routeKinds;
    private int _routeCount;
    private OpenApiOptions _openApiOptions;

    /// <summary>Creates a host with default options on <paramref name="port"/>.</summary>
    public WebHost(string name, ushort port)
        : this(name, NewOptions(port))
    {
    }

    /// <summary>Creates a host; call Start()/Tick()/Stop() like a service.</summary>
    public WebHost(string name, KestrelServerOptions options)
    {
        Name = name;
        Options = options ?? new KestrelServerOptions();
        Port = Options.Port;

        // Clamp the option-derived sizes so a bad configuration cannot
        // break the engine's fixed arrays.
        int conns = Options.MaxConnections;
        if (conns < 1) conns = 1;
        if (conns > 16) conns = 16;
        _maxConnections = conns;

        int reqBytes = Options.MaxRequestBytes;
        if (reqBytes < 256) reqBytes = 256;
        if (reqBytes > 65536) reqBytes = 65536;
        _maxRequestBytes = reqBytes;

        int routes = Options.MaxRoutes;
        if (routes < 1) routes = 1;
        if (routes > 256) routes = 256;
        _maxRoutes = routes;

        int segs = Options.MaxPathSegments;
        if (segs < 2) segs = 2;
        if (segs > 16) segs = 16;
        _maxPathSegments = segs;

        int perConn = Options.MaxRequestsPerConnection;
        if (perConn < 1) perConn = 1;
        _maxRequestsPerConnection = perConn;

        ulong idle = Options.KeepAliveTimeoutMs;
        if (idle < 1000) idle = 1000;
        _idleTimeoutMs = idle;

        _connections = new Connection[_maxConnections];
        _routeMethods = new string[_maxRoutes];
        _routePatterns = new string[_maxRoutes];
        _routeHandlers = new RequestHandler[_maxRoutes];
        _routeAsyncHandlers = new RequestHandlerAsync[_maxRoutes];
        _routeKinds = new int[_maxRoutes];
    }

    private static KestrelServerOptions NewOptions(ushort port)
    {
        var options = new KestrelServerOptions();
        options.Port = port;
        return options;
    }

    /// <summary>True while the listener is running.</summary>
    public bool IsRunning => _active;

    /// <summary>Requests handled since the last Start().</summary>
    public int RequestCount => _requests;

    /// <summary>Register a handler for a method + pattern ("/x/{id}").</summary>
    public void AddRoute(string method, string pattern, RequestHandler handler)
    {
        if (_routeCount >= _maxRoutes)
            return;
        _routeMethods[_routeCount] = method;
        _routePatterns[_routeCount] = pattern;
        _routeHandlers[_routeCount] = handler;
        _routeCount++;
    }

    /// <summary>Register an async handler for a method + pattern.</summary>
    public void AddRouteAsync(string method, string pattern, RequestHandlerAsync handler)
    {
        if (_routeCount >= _maxRoutes)
            return;
        _routeMethods[_routeCount] = method;
        _routePatterns[_routeCount] = pattern;
        _routeAsyncHandlers[_routeCount] = handler;
        _routeCount++;
    }

    // ====================================================================
    // OpenAPI document endpoint (M6: app.MapOpenApi())
    // ====================================================================

    /// <summary>A normal handler route (the default kind).</summary>
    internal const int RouteKindHandler = 0;

    /// <summary>The generated OpenAPI document route (no handler; M6).</summary>
    internal const int RouteKindOpenApi = 1;

    /// <summary>Register the OpenAPI document endpoint (MapOpenApi).</summary>
    internal void AddOpenApiRoute(string pattern, OpenApiOptions options)
    {
        _openApiOptions = options;
        if (_routeCount >= _maxRoutes)
            return;
        _routeMethods[_routeCount] = "GET";
        _routePatterns[_routeCount] = pattern;
        _routeKinds[_routeCount] = RouteKindOpenApi;
        _routeCount++;
    }

    /// <summary>The generated OpenAPI JSON for the current route table (M6).</summary>
    internal string BuildOpenApiJson()
    {
        return OpenApiDocument.Build(this, _openApiOptions);
    }

    // Route-table accessors for the OpenAPI generator (M6 plumbing).
    internal int RouteCount => _routeCount;
    internal string RouteMethodAt(int index) => _routeMethods[index];
    internal string RoutePatternAt(int index) => _routePatterns[index];
    internal int RouteKindAt(int index) => _routeKinds[index];

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

        // M5: optional HTTPS (TLS 1.3) endpoint on the same engine; the
        // shared Ed25519 certificate is loaded/generated by WebService,
        // and the P-256 pair (Schannel-compatible) is served to clients
        // that do not offer ed25519.
        if (Options.HttpsEnabled)
        {
            if (WebService.EnsureCertificate(out byte[] certDer, out byte[] keySeed,
                out byte[] p256CertDer, out byte[] p256Key))
            {
                _httpsListener = new SocketConnectionListener(_stack,
                    new ConnectionEndPoint(Options.HttpsPort), certDer, keySeed,
                    p256CertDer, p256Key);
                if (!_httpsListener.Start())
                {
                    _httpsListener = null;
                    Console.WriteLine("[" + Name + "] https bind failed on port " + WebService.IntToStr(Options.HttpsPort));
                }
            }
            else
            {
                Console.WriteLine("[" + Name + "] https disabled: no certificate");
            }
        }

        for (int i = 0; i < _connections.Length; i++)
            _connections[i] = null;
        _requests = 0;
        _startedAt = Timer.GetUptimeMilliseconds();
        _active = true;
        Console.WriteLine("[" + Name + "] listening on port " + WebService.IntToStr(Port) +
            (_httpsListener != null ? " (http) and " + WebService.IntToStr(Options.HttpsPort) + " (https, TLS 1.3)" : "") +
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
        if (_httpsListener != null)
        {
            _httpsListener.UnbindAsync();
            _httpsListener.DisposeAsync();
            _httpsListener = null;
        }
        _active = false;
        Console.WriteLine("[" + Name + "] stopped");
    }

    /// <summary>One bounded work slice (called from the shell idle hook).</summary>
    public void Tick()
    {
        if (!_active)
            return;

        // Accept new connections (stack pump + pending accepts), HTTP and HTTPS.
        AcceptFrom(_listener);
        AcceptFrom(_httpsListener);

        // Service each connection: pump the transport, then HTTP.
        ulong now = Timer.GetUptimeMilliseconds();
        for (int i = 0; i < _maxConnections; i++)
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
    // Accept plumbing
    // ====================================================================

    /// <summary>Pump a listener and move accepted sockets into the connection pool.</summary>
    private void AcceptFrom(SocketConnectionListener listener)
    {
        if (listener == null)
            return;
        listener.Tick();
        while (listener.TryAccept(out SocketConnectionContext? accepted))
        {
            if (accepted == null)
                break;
            int slot = -1;
            for (int i = 0; i < _maxConnections; i++)
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
            _connections[slot] = new Connection(accepted, _maxRequestBytes);
        }
    }

    // ====================================================================
    // Connection handling
    // ====================================================================

    /// <summary>One connection slot: transport + request buffer + state.</summary>
    private sealed class Connection
    {
        public readonly SocketConnectionContext Context;
        public readonly byte[] Buffer;
        public readonly char[] BodyChars;
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

        // M5: HTTP/2 + TLS state for this connection.
        public Http2Connection H2;              // non-null once in h2 mode
        public H2Bridge Bridge;                 // the owner adapter H2 uses
        public bool TlsChecked;                 // handshake observed once

        public Connection(SocketConnectionContext context, int maxRequestBytes)
        {
            Context = context;
            Buffer = new byte[maxRequestBytes];
            BodyChars = new char[maxRequestBytes];
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
        if (!conn.Done && now - conn.LastActivity > _idleTimeoutMs)
        {
            conn.Released = true;
            return;
        }

        // Pump the transport (socket <-> pipes) first.
        conn.Context.Tick();
        if (conn.Done)
            return;   // keep pumping above until IsFinished / grace expiry

        // M5: TLS handshake gate. The app-facing pipes are empty until the
        // TLS 1.3 handshake completes (Context.Tick drives it). An ALPN
        // "h2" result hands the connection to the shared HTTP/2 engine -
        // the client's connection preface arrives right after it.
        if (conn.Context.TlsEnabled)
        {
            if (!conn.Context.TlsConnected)
                return;   // handshake still in flight
            if (!conn.TlsChecked)
            {
                conn.TlsChecked = true;
                if (WebService.StrEq(conn.Context.TlsAlpn, "h2"))
                    StartH2(conn, null, 0);
            }
        }

        // M5: once in HTTP/2 mode the engine owns the transport (it pulls
        // from the input pipe and writes to the output pipe itself).
        if (conn.H2 != null)
        {
            conn.H2.Tick();
            if (conn.H2.Closed)
            {
                conn.Released = true;
                return;
            }
            if (conn.Context.PeerClosed && !conn.H2.HasOpenWork())
                conn.Released = true;
            conn.LastActivity = now;
            return;
        }

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

        // M5: HTTP/2 cleartext (h2c prior knowledge). A plain connection
        // whose first bytes are the client preface switches protocols
        // before any HTTP/1.x parsing (curl --http2-prior-knowledge).
        if (conn.H2 == null && !conn.Context.TlsEnabled && conn.Length >= 3 &&
            conn.Buffer[0] == (byte)'P' && conn.Buffer[1] == (byte)'R' && conn.Buffer[2] == (byte)'I')
        {
            if (conn.Length < Http2Connection.ClientPreface.Length)
                return;   // wait for the rest of the preface
            StartH2(conn, conn.Buffer, conn.Length);
            conn.Length = 0;
            conn.H2.Tick();
            if (conn.H2.Closed)
                conn.Released = true;
            return;
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
            // Expect: 100-continue is HTTP/1.1-only (RFC 7231: a 1.0 client's
            // expectation is ignored); any other expectation is unsatisfiable.
            // The per-request cap plus this single interim send bound the
            // 100-continue traffic on a connection.
            bool expectContinue = http11 && (flags & HttpParser.FlagExpectContinue) != 0;
            if (http11 && (flags & HttpParser.FlagExpectUnsupported) != 0)
            {
                RespondAndFinish(conn, 417, "text/plain", "expectation failed\n");
                return;
            }

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

            bool keepAlive = http11 && !connClose && conn.Requests < _maxRequestsPerConnection;
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

        int matched = MatchRoute(method, path);
        bool pathMatched = _matchPathMatched;
        string[] capNames = _matchCapNames;
        string[] capValues = _matchCapValues;
        int capCount = _matchCapCount;

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
        if (_routeKinds[matched] == RouteKindOpenApi)
        {
            // M6: the endpoint is generated, not handler-backed.
            ctx.Response.Json(BuildOpenApiJson());
        }
        else if (sync != null)
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
    // Route matching (shared by HTTP/1.1 dispatch and the HTTP/2 bridge)
    // ====================================================================

    /// <summary>
    /// Match the route table for method+path. Returns the route index or
    /// -1. Results land in the _match* fields (PathMatched distinguishes
    /// 405 from 404; capNames/capValues/capCount carry captured segments).
    /// NOTE: multi-out-parameter calls are avoided deliberately - the
    /// Tier-0 JIT has been observed to deliver a stale out slot on a
    /// 4-out call (pathMatched came back true for an unmatched path,
    /// turning 404s into 405s); fields keep the engine deterministic.
    /// </summary>
    private bool _matchPathMatched;
    private string[] _matchCapNames;
    private string[] _matchCapValues;
    private int _matchCapCount;

    private int MatchRoute(string method, string path)
    {
        var pseg = new string[_maxPathSegments];
        int pcount;
        SplitPath(path, pseg, out pcount);

        int matched = -1;
        _matchPathMatched = false;
        _matchCapNames = null;
        _matchCapValues = null;
        _matchCapCount = 0;

        for (int i = 0; i < _routeCount; i++)
        {
            var rseg = new string[_maxPathSegments];
            int rcount;
            SplitPath(_routePatterns[i], rseg, out rcount);
            if (rcount != pcount)
                continue;

            var names = new string[_maxPathSegments];
            var values = new string[_maxPathSegments];
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

            _matchPathMatched = true;
            if (WebService.StrEq(_routeMethods[i], method))
            {
                matched = i;
                _matchCapNames = names;
                _matchCapValues = values;
                _matchCapCount = ncapt;
                break;
            }
        }
        return matched;
    }

    // ====================================================================
    // HTTP/2 (M5): the shared DDK h2 engine over this engine's pipes
    // ====================================================================

    /// <summary>Hand a connection to the shared HTTP/2 engine, seeding any bytes already read.</summary>
    private void StartH2(Connection conn, byte[] seed, int seedLen)
    {
        conn.Bridge = new H2Bridge(conn, this);
        conn.H2 = new Http2Connection(conn.Bridge);
        if (seed != null && seedLen > 0)
            conn.H2.Seed(seed, 0, seedLen);
        Console.WriteLine("[" + Name + "] http/2 connection (" +
            (conn.Context.TlsEnabled ? "ALPN h2" : "prior knowledge") + ") - shared DDK h2 engine");
    }

    /// <summary>
    /// M5: build a response for the HTTP/2 engine from this host's route
    /// table. Synchronous handlers only: an async handler that suspends
    /// gets 503 (deferred HTTP/2 responses are a later milestone).
    /// </summary>
    internal bool TryBuildH2Response(string method, string path, string reqBody,
        out int status, out string statusText, out string contentType, out string responseBody)
    {
        status = 200;
        statusText = "OK";
        contentType = "text/plain";
        responseBody = "";

        string query = "";
        int qm = WebService.IndexOfChar(path, '?');
        if (qm >= 0)
        {
            query = path.Substring(qm + 1, path.Length - qm - 1);
            path = path.Substring(0, qm);
        }

        int matched = MatchRoute(method, path);
        bool pathMatched = _matchPathMatched;
        string[] capNames = _matchCapNames;
        string[] capValues = _matchCapValues;
        int capCount = _matchCapCount;

        if (matched < 0)
        {
            if (pathMatched)
            {
                status = 405;
                statusText = "Method Not Allowed";
                responseBody = "method not allowed\n";
            }
            else
            {
                status = 404;
                statusText = "Not Found";
                contentType = "application/json";
                responseBody = "{\"error\":\"not found\"}\n";
            }
            return true;
        }

        _requests++;

        var ctx = new HttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.QueryString = query;
        ctx.Request.Body = reqBody;
        ctx.Request.SetRoute(capNames, capValues, capCount);

        RequestHandler sync = _routeHandlers[matched];
        if (_routeKinds[matched] == RouteKindOpenApi)
        {
            // M6: the endpoint is generated, not handler-backed.
            ctx.Response.Json(BuildOpenApiJson());
        }
        else if (sync != null)
        {
            sync(ctx);
        }
        else
        {
            Task deferred = _routeAsyncHandlers[matched](ctx);
            if (!deferred.IsCompleted)
            {
                status = 503;
                statusText = "Service Unavailable";
                contentType = "text/plain";
                responseBody = "async handler not supported over HTTP/2 yet\n";
                return true;
            }
        }

        status = ctx.Response.StatusCode;
        statusText = StatusText(status);
        contentType = ctx.Response.ContentType;
        responseBody = ctx.Response.Body;
        return true;
    }

    /// <summary>Reason phrase for a status code (h2 header value).</summary>
    private static string StatusText(int status)
    {
        if (status == 200) return "OK";
        if (status == 201) return "Created";
        if (status == 204) return "No Content";
        if (status == 400) return "Bad Request";
        if (status == 401) return "Unauthorized";
        if (status == 403) return "Forbidden";
        if (status == 404) return "Not Found";
        if (status == 405) return "Method Not Allowed";
        if (status == 429) return "Too Many Requests";
        if (status == 500) return "Internal Server Error";
        if (status == 503) return "Service Unavailable";
        return "OK";
    }

    /// <summary>
    /// M5: adapts the connection's plaintext pipes (post-TLS when
    /// applicable) to the IH2Owner contract and routes engine requests to
    /// this host's response builder.
    /// </summary>
    private sealed class H2Bridge : IH2Owner
    {
        private readonly Connection _conn;
        private readonly WebHost _host;
        private readonly byte[] _left = new byte[16384];
        private int _leftLen;
        private bool _inputCompleted;

        public H2Bridge(Connection conn, WebHost host)
        {
            _conn = conn;
            _host = host;
        }

        public int H2Read(byte[] buf, int off, int len)
        {
            if (_leftLen == 0)
            {
                PipeReader reader = _conn.Context.InputReader;
                if (!reader.TryRead(out ReadResult result))
                    return _inputCompleted ? -1 : 0;
                ReadOnlySequence<byte> buffer = result.Buffer;
                if (!buffer.IsEmpty)
                {
                    ReadOnlyMemory<byte> mem = buffer.First;
                    ReadOnlySpan<byte> src = mem.Span;
                    int copy = src.Length < _left.Length ? src.Length : _left.Length;
                    for (int i = 0; i < copy; i++)
                        _left[i] = src[i];
                    _leftLen = copy;
                    if ((long)copy == buffer.Length)
                        reader.AdvanceTo(buffer.End);
                    else
                        reader.AdvanceTo(buffer.GetPosition(copy));
                }
                if (result.IsCompleted)
                    _inputCompleted = true;
                if (_leftLen == 0)
                    return _inputCompleted ? -1 : 0;
            }

            int n = _leftLen < len ? _leftLen : len;
            for (int i = 0; i < n; i++)
                buf[off + i] = _left[i];
            for (int i = n; i < _leftLen; i++)
                _left[i - n] = _left[i];
            _leftLen -= n;
            return n;
        }

        public bool H2Write(byte[] buf, int off, int len)
        {
            PipeWriter writer = _conn.Context.OutputWriter;
            Span<byte> span = writer.GetSpan(len);
            for (int i = 0; i < len; i++)
                span[i] = buf[off + i];
            writer.Advance(len);
            ValueTask<FlushResult> flush = writer.FlushAsync();
            if (flush.IsCompleted)
                return !flush.Result.IsCompleted;
            return true;
        }

        public uint H2PeerIp => _conn.Context.PeerIp;

        public bool H2BuildResponse(string method, string path, string body,
            out int status, out string statusText, out string contentType, out string responseBody)
            => _host.TryBuildH2Response(method, path, body, out status, out statusText, out contentType, out responseBody);
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
    private void SplitPath(string s, string[] segs, out int count)
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
                if (count >= _maxPathSegments)
                    return;   // too deep: caller treats it as no match
                segs[count] = s.Substring(start, i - start);
                count++;
                start = i + 1;
            }
            i++;
        }
    }
}
