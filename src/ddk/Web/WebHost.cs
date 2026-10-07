// NeutrinoOS Web - Kestrel port Milestone 1: the host engine.
//
// WebHost owns a TcpServer listener plus a small pool of connection slots
// and drives them from the cooperative service Tick() (kernel idle hook):
// no threads, no async yet. It is the M1 stand-in for KestrelServer /
// Kestrel's HttpConnection; the route table and request/response surface
// in front of it are the shapes the real port will keep.
//
// The request path handling (head parse, Content-Length body wait, 431/413
// limits, one request per connection, Connection: close) is the proven
// logic from src/ddk/Services/SampleApi.cs, generalized with a route table.
//
// See docs/KESTREL-PORT.md for the milestone plan (M2 moves this onto
// System.IO.Pipelines + the async runtime; M3 replaces it with the real
// Kestrel Core sources).

using System;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Sockets;
using NeutrinoOS.DDK.Network.Stack;
using NeutrinoOS.DDK.Services;   // WebService's proven string helpers (same assembly)

namespace NeutrinoOS.DDK.Web;

/// <summary>The M1 HTTP/1.1 host engine (cooperative, one request per connection).</summary>
public sealed class WebHost
{
    private const int MaxConnections = 4;
    private const int MaxRequestBytes = 4096;   // head + body must fit
    private const int MaxRoutes = 32;
    private const int MaxPathSegments = 8;      // per pattern/path
    private const ulong IdleTimeoutMs = 10000;

    /// <summary>Log prefix, e.g. "webapi" (prints as [webapi] ...).</summary>
    public string Name;

    /// <summary>TCP port to bind (default set by WebApplication: 8080).</summary>
    public ushort Port;

    private TcpServer _listener;
    private NetworkStack _stack;
    private readonly Connection[] _connections = new Connection[MaxConnections];
    private bool _active;
    private ulong _startedAt;
    private int _requests;

    private readonly string[] _routeMethods = new string[MaxRoutes];
    private readonly string[] _routePatterns = new string[MaxRoutes];
    private readonly RequestHandler[] _routeHandlers = new RequestHandler[MaxRoutes];
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

        _listener = new TcpServer(_stack, Port, true);
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
                conn.Socket.Close();
                _connections[i] = null;
            }
        }
        if (_listener != null)
        {
            _listener.Stop();
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

        // Drive the network stack (receive + transmit) cooperatively.
        NetworkPump.Pump(_stack, 4);

        // Accept up to the whole pool of new connections per slice.
        while (_listener.Pending())
        {
            var sock = _listener.Accept();
            if (sock == null)
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
                sock.Close();   // pool full: drop
                continue;
            }
            _connections[slot] = new Connection(sock);
        }

        // Service each connection: read, route, respond, close.
        ulong now = Timer.GetUptimeMilliseconds();
        for (int i = 0; i < MaxConnections; i++)
        {
            var conn = _connections[i];
            if (conn == null)
                continue;
            ServiceConnection(conn, now);
            if (conn.Released)
            {
                conn.Socket.Close();
                _connections[i] = null;
            }
        }

        // Flush queued responses even when no inbound frame arrived.
        NetworkPump.FlushTx(_stack);
    }

    // ====================================================================
    // Connection handling
    // ====================================================================

    /// <summary>One connection slot: socket + request buffer + state.</summary>
    private sealed class Connection
    {
        public readonly TcpSocket Socket;
        public readonly byte[] Buffer = new byte[MaxRequestBytes];
        public int Length;
        public ulong LastActivity;
        public bool Done;        // response written; close on the next step
        public bool Released;    // socket closed; slot can be reused

        public Connection(TcpSocket socket)
        {
            Socket = socket;
            LastActivity = Timer.GetUptimeMilliseconds();
        }
    }

    private unsafe void ServiceConnection(Connection conn, ulong now)
    {
        if (now - conn.LastActivity > IdleTimeoutMs)
        {
            conn.Released = true;
            return;
        }
        if (conn.Done)
        {
            conn.Released = true;
            return;
        }

        // Non-blocking read; whatever arrives accumulates in the buffer.
        if (conn.Length < conn.Buffer.Length)
        {
            int got;
            fixed (byte* p = conn.Buffer)
            {
                got = conn.Socket.Receive(p + conn.Length, conn.Buffer.Length - conn.Length);
            }
            if (got > 0)
            {
                conn.Length += got;
                conn.LastActivity = now;
            }
        }

        int headerEnd = FindHeaderEnd(conn.Buffer, conn.Length);
        if (headerEnd < 0)
        {
            // No complete head yet. If the buffer is full it never will be.
            if (conn.Length >= conn.Buffer.Length)
                RespondRaw(conn, 431, "text/plain", "request too large\n");
            return;
        }

        string head = CharsOf(conn.Buffer, 0, headerEnd);
        string method;
        string path;
        int contentLength;
        ParseHead(head, out method, out path, out contentLength);
        if (method == null)
        {
            RespondRaw(conn, 400, "text/plain", "bad request\n");
            return;
        }

        int total = headerEnd + 4 + contentLength;
        if (total > conn.Buffer.Length)
        {
            RespondRaw(conn, 413, "text/plain", "body too large\n");
            return;
        }
        if (conn.Length < total)
            return;   // keep waiting for the rest of the body

        string reqBody = "";
        if (contentLength > 0)
            reqBody = CharsOf(conn.Buffer, headerEnd + 4, contentLength);

        Dispatch(conn, method, path, reqBody);
    }

    /// <summary>Route one complete request and write the response.</summary>
    private void Dispatch(Connection conn, string method, string target, string reqBody)
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
                RespondRaw(conn, 405, "text/plain", "method not allowed\n");
            else
                RespondRaw(conn, 404, "application/json", "{\"error\":\"not found\"}\n");
            return;
        }

        var ctx = new HttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.QueryString = query;
        ctx.Request.Body = reqBody;
        ctx.Request.SetRoute(capNames, capValues, capCount);

        _routeHandlers[matched](ctx);

        RespondRaw(conn, ctx.Response.StatusCode, ctx.Response.ContentType, ctx.Response.Body);
    }

    // ====================================================================
    // HTTP I/O helpers (proven logic from SampleApi.cs)
    // ====================================================================

    /// <summary>Write a complete response and mark the connection done.</summary>
    private static unsafe void RespondRaw(Connection conn, int status, string contentType, string body)
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
            "Connection: close\r\n\r\n";
        var bytes = new byte[head.Length + body.Length];
        for (int i = 0; i < head.Length; i++)
            bytes[i] = (byte)head[i];
        for (int i = 0; i < body.Length; i++)
            bytes[head.Length + i] = (byte)body[i];

        int sent = 0;
        while (sent < bytes.Length)
        {
            int got;
            fixed (byte* p = bytes)
            {
                got = conn.Socket.Send(p + sent, bytes.Length - sent);
            }
            if (got <= 0)
                break;
            sent += got;
        }
        conn.Done = true;
    }

    /// <summary>First line + header lines (chars [off..off+len)).</summary>
    private static void ParseHead(string head, out string method, out string path, out int contentLength)
    {
        method = null;
        path = null;
        contentLength = 0;

        int lineEnd = IndexOfStr(head, "\r\n", 0);
        if (lineEnd < 0)
            return;
        string line = head.Substring(0, lineEnd);
        int sp1 = WebService.IndexOfChar(line, ' ');
        if (sp1 <= 0)
            return;
        int sp2 = WebService.IndexOfChar2(line, ' ', sp1 + 1);
        if (sp2 <= 0)
            return;
        method = line.Substring(0, sp1);
        path = line.Substring(sp1 + 1, sp2 - sp1 - 1);

        int pos = lineEnd + 2;
        while (pos < head.Length)
        {
            int end = IndexOfStr(head, "\r\n", pos);
            if (end < 0)
                end = head.Length;   // final header: its CRLF is outside head
            string h = head.Substring(pos, end - pos);
            if (h.Length == 0)
                break;
            int colon = WebService.IndexOfChar(h, ':');
            if (colon > 0)
            {
                string name = WebService.TrimStr(h.Substring(0, colon));
                if (NameEq(name, "content-length"))
                {
                    string value = WebService.TrimStr(h.Substring(colon + 1, h.Length - colon - 1));
                    contentLength = WebService.ParseInt(value, 0) & 0xFFFFF;
                }
            }
            pos = end + 2;
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

    /// <summary>Index of CRLFCRLF in the buffer, or -1.</summary>
    private static int FindHeaderEnd(byte[] buf, int len)
    {
        for (int i = 0; i + 3 < len; i++)
        {
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                return i;
        }
        return -1;
    }

    private static string CharsOf(byte[] buf, int off, int len)
    {
        var chars = new char[len];
        for (int i = 0; i < len; i++)
            chars[i] = (char)buf[off + i];
        return new string(chars);
    }

    private static int IndexOfStr(string s, string needle, int start)
    {
        for (int i = start; i + needle.Length <= s.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (s[i + j] != needle[j])
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

    private static bool NameEq(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i];
            char cb = b[i];
            if (ca >= 'A' && ca <= 'Z')
                ca = (char)(ca + 32);
            if (ca != cb)
                return false;
        }
        return true;
    }
}
