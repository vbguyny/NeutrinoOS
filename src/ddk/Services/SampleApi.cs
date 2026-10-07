// NeutrinoOS DDK - Sample REST API service (sample code, v1.0)
//
// A complete, self-contained example of how to host a REST API on
// NeutrinoOS. It follows the same service shape as the production web
// host (see WebService.cs) but keeps everything in one commented file so
// it can be used as a template for your own APIs:
//
//   * Start()/Tick()/Stop() - the cooperative service contract. The
//     kernel's ServiceRegistry (src/kernel/Services/ServiceRegistry.cs)
//     JIT-compiles these from the DDK assembly and calls Tick() from the
//     shell idle hook. There are no threads: Tick() must do a bounded
//     amount of work and never block.
//   * A TcpServer listener on port 8080 and a small pool of connection
//     slots. Each connection buffers one request, routes it, answers and
//     closes ("Connection: close" - keep-alive is left out to keep the
//     sample small).
//   * A tiny router: match method + path, build a JSON response.
//
// Demo endpoints (all JSON):
//   GET    /                      service blurb + endpoint index
//   GET    /api/v1/info           uptime, request count, task count
//   GET    /api/v1/echo?msg=...   query-string demo
//   POST   /api/v1/echo           request-body demo (echoes the bytes)
//   GET    /api/v1/tasks          list tasks
//   POST   /api/v1/tasks          create (JSON {"name":"..."} or form name=...)
//   GET    /api/v1/tasks/{id}     read one task          (404 when missing)
//   PUT    /api/v1/tasks/{id}     rename one task        (404 when missing)
//   DELETE /api/v1/tasks/{id}     delete one task        (404 when missing)
//
// Run it from the NeutrinoOS shell:   sampleapi start   (stop | status)
// Reach it from Windows: boot QEMU with
//   -netdev user,id=n0,hostfwd=tcp::18080-:8080
// then use http://127.0.0.1:18080 . Full instructions and a ready-made
// Postman collection: docs/samples/rest-api/README.md

using System;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Sockets;
using NeutrinoOS.DDK.Network.Stack;

namespace NeutrinoOS.DDK.Services;

/// <summary>The sample REST API service (see file header).</summary>
public static class SampleApi
{
    private const ushort Port = 8080;
    private const int MaxConnections = 4;
    private const int MaxRequestBytes = 4096;   // headers + body must fit
    private const int MaxTasks = 8;
    private const int MaxTaskNameLength = 64;
    private const ulong IdleTimeoutMs = 10000;

    private static TcpServer _listener;
    private static NetworkStack _stack;
    private static readonly SampleConnection[] _connections = new SampleConnection[MaxConnections];
    private static bool _active;
    private static ulong _startedAt;
    private static int _requests;

    // In-memory task store: null = free slot, id = slot + 1.
    private static readonly string[] _taskNames = new string[MaxTasks];
    private static int _taskCount;

    /// <summary>True while the listener is running.</summary>
    public static bool Active => _active;

    // ====================================================================
    // Service contract (called by the kernel ServiceRegistry)
    // ====================================================================

    /// <summary>Start the service; 0 = ok.</summary>
    public static int Start()
    {
        if (_active)
            return 0;

        var eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.WriteLine("[sampleapi] no eth0 interface - start QEMU with a NIC");
            return -30;
        }
        _stack = eth.Stack;

        _listener = new TcpServer(_stack, Port, true);
        if (!_listener.Start())
        {
            _listener = null;
            Console.WriteLine("[sampleapi] bind failed on port 8080");
            return -32;
        }

        for (int i = 0; i < _connections.Length; i++)
            _connections[i] = null;
        for (int i = 0; i < _taskNames.Length; i++)
            _taskNames[i] = null;
        _taskCount = 0;
        _requests = 0;
        _startedAt = Timer.GetUptimeMilliseconds();
        _active = true;
        Console.WriteLine("[sampleapi] listening on port 8080 - try GET /api/v1/info");
        return 0;
    }

    /// <summary>Stop the service and drop all connections.</summary>
    public static void Stop()
    {
        if (!_active)
            return;
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i] != null)
            {
                _connections[i].Socket.Close();
                _connections[i] = null;
            }
        }
        if (_listener != null)
        {
            _listener.Stop();
            _listener = null;
        }
        _active = false;
        Console.WriteLine("[sampleapi] stopped");
    }

    /// <summary>One bounded work slice (called from the shell idle hook).</summary>
    public static void Tick()
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
                sock.Close();   // pool full: drop (a real service would answer 503)
                continue;
            }
            _connections[slot] = new SampleConnection(sock);
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
    private sealed class SampleConnection
    {
        public readonly TcpSocket Socket;
        public readonly byte[] Buffer = new byte[MaxRequestBytes];
        public int Length;
        public ulong LastActivity;
        public bool Done;        // response written; close on the next step
        public bool Released;    // socket closed; slot can be reused

        public SampleConnection(TcpSocket socket)
        {
            Socket = socket;
            LastActivity = Timer.GetUptimeMilliseconds();
        }
    }

    private static unsafe void ServiceConnection(SampleConnection conn, ulong now)
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
                Respond(conn, 431, "Request Header Fields Too Large", "text/plain", "request too large\n");
            return;
        }

        string head = CharsOf(conn.Buffer, 0, headerEnd);
        string method;
        string path;
        int contentLength;
        ParseHead(head, out method, out path, out contentLength);
        if (method == null)
        {
            Respond(conn, 400, "Bad Request", "text/plain", "bad request\n");
            return;
        }

        int total = headerEnd + 4 + contentLength;
        if (total > conn.Buffer.Length)
        {
            Respond(conn, 413, "Payload Too Large", "text/plain", "body too large\n");
            return;
        }
        if (conn.Length < total)
            return;   // keep waiting for the rest of the body

        string reqBody = "";
        if (contentLength > 0)
            reqBody = CharsOf(conn.Buffer, headerEnd + 4, contentLength);

        _requests++;
        Route(conn, method, path, reqBody);
    }

    // ====================================================================
    // The REST API itself
    // ====================================================================

    private static void Route(SampleConnection conn, string method, string path, string reqBody)
    {
        // Split off any query string (?k=v&...).
        string query = "";
        int q = WebService.IndexOfChar(path, '?');
        if (q >= 0)
        {
            query = path.Substring(q + 1, path.Length - q - 1);
            path = path.Substring(0, q);
        }

        bool isGet = WebService.StrEq(method, "GET");
        bool isPost = WebService.StrEq(method, "POST");
        bool isPut = WebService.StrEq(method, "PUT");
        bool isDelete = WebService.StrEq(method, "DELETE");

        // GET / - humans land here; describe the API.
        if (isGet && WebService.StrEq(path, "/"))
        {
            Respond(conn, 200, "OK", "application/json",
                "{\"service\":\"NeutrinoOS sample REST API\",\"port\":8080," +
                "\"endpoints\":[\"GET /api/v1/info\",\"GET /api/v1/echo?msg=...\"," +
                "\"POST /api/v1/echo\",\"GET /api/v1/tasks\",\"POST /api/v1/tasks\"," +
                "\"GET /api/v1/tasks/{id}\",\"PUT /api/v1/tasks/{id}\"," +
                "\"DELETE /api/v1/tasks/{id}\"]}\n");
            return;
        }

        // GET /api/v1/info - counters.
        if (isGet && WebService.StrEq(path, "/api/v1/info"))
        {
            int uptime = (int)((Timer.GetUptimeMilliseconds() - _startedAt) / 1000);
            Respond(conn, 200, "OK", "application/json",
                "{\"service\":\"sampleapi\",\"version\":\"1.0.0\",\"uptime_s\":" +
                WebService.IntToStr(uptime) +
                ",\"requests\":" + WebService.IntToStr(_requests) +
                ",\"tasks\":" + WebService.IntToStr(_taskCount) + "}\n");
            return;
        }

        // GET /api/v1/echo?msg=hello - query-string demo.
        if (isGet && WebService.StrEq(path, "/api/v1/echo"))
        {
            string msg = QueryValue(query, "msg");
            Respond(conn, 200, "OK", "application/json",
                "{\"method\":\"GET\",\"msg\":\"" + JsonEscape(msg ?? "") + "\"}\n");
            return;
        }

        // POST /api/v1/echo - raw request-body demo.
        if (isPost && WebService.StrEq(path, "/api/v1/echo"))
        {
            Respond(conn, 200, "OK", "application/json",
                "{\"method\":\"POST\",\"content_length\":" + WebService.IntToStr(reqBody.Length) +
                ",\"you_sent\":\"" + JsonEscape(reqBody) + "\"}\n");
            return;
        }

        // /api/v1/tasks - the CRUD collection.
        if (WebService.StrEq(path, "/api/v1/tasks"))
        {
            if (isGet)
            {
                Respond(conn, 200, "OK", "application/json", TasksJson());
                return;
            }
            if (isPost)
            {
                string name = ExtractTaskName(reqBody);
                if (name == null || name.Length == 0)
                {
                    Respond(conn, 400, "Bad Request", "application/json",
                        "{\"error\":\"name required\"}\n");
                    return;
                }
                if (name.Length > MaxTaskNameLength)
                    name = name.Substring(0, MaxTaskNameLength);
                int slot = -1;
                for (int i = 0; i < MaxTasks; i++)
                {
                    if (_taskNames[i] == null)
                    {
                        slot = i;
                        break;
                    }
                }
                if (slot < 0)
                {
                    Respond(conn, 409, "Conflict", "application/json",
                        "{\"error\":\"task store full\"}\n");
                    return;
                }
                _taskNames[slot] = name;
                _taskCount++;
                Respond(conn, 201, "Created", "application/json", TaskJson(slot) + "\n");
                return;
            }
            Respond(conn, 405, "Method Not Allowed", "text/plain", "method not allowed\n");
            return;
        }

        // /api/v1/tasks/{id} - one task.
        int taskId;
        if (ParseTaskId(path, out taskId))
        {
            bool exists = _taskNames[taskId - 1] != null;
            if (isGet)
            {
                if (exists)
                    Respond(conn, 200, "OK", "application/json", TaskJson(taskId - 1) + "\n");
                else
                    Respond(conn, 404, "Not Found", "application/json", "{\"error\":\"not found\"}\n");
                return;
            }
            if (isPut)
            {
                if (!exists)
                {
                    Respond(conn, 404, "Not Found", "application/json", "{\"error\":\"not found\"}\n");
                    return;
                }
                string name = ExtractTaskName(reqBody);
                if (name == null || name.Length == 0)
                {
                    Respond(conn, 400, "Bad Request", "application/json",
                        "{\"error\":\"name required\"}\n");
                    return;
                }
                if (name.Length > MaxTaskNameLength)
                    name = name.Substring(0, MaxTaskNameLength);
                _taskNames[taskId - 1] = name;
                Respond(conn, 200, "OK", "application/json", TaskJson(taskId - 1) + "\n");
                return;
            }
            if (isDelete)
            {
                if (exists)
                {
                    _taskNames[taskId - 1] = null;
                    _taskCount--;
                    Respond(conn, 200, "OK", "application/json",
                        "{\"deleted\":" + WebService.IntToStr(taskId) + "}\n");
                }
                else
                {
                    Respond(conn, 404, "Not Found", "application/json", "{\"error\":\"not found\"}\n");
                }
                return;
            }
            Respond(conn, 405, "Method Not Allowed", "text/plain", "method not allowed\n");
            return;
        }

        Respond(conn, 404, "Not Found", "application/json", "{\"error\":\"not found\"}\n");
    }

    /// <summary>JSON for one task slot: {"id":N,"name":"..."}.</summary>
    private static string TaskJson(int slot)
    {
        return "{\"id\":" + WebService.IntToStr(slot + 1) +
               ",\"name\":\"" + JsonEscape(_taskNames[slot]) + "\"}";
    }

    /// <summary>JSON for the whole collection.</summary>
    private static string TasksJson()
    {
        string body = "{\"count\":" + WebService.IntToStr(_taskCount) + ",\"tasks\":[";
        bool first = true;
        for (int i = 0; i < MaxTasks; i++)
        {
            if (_taskNames[i] == null)
                continue;
            if (!first)
                body += ",";
            first = false;
            body += TaskJson(i);
        }
        return body + "]}\n";
    }

    /// <summary>"/api/v1/tasks/&lt;digits&gt;" with 1 &lt;= id &lt;= MaxTasks.</summary>
    private static bool ParseTaskId(string path, out int id)
    {
        id = 0;
        const string prefix = "/api/v1/tasks/";
        if (path.Length <= prefix.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (path[i] != prefix[i])
                return false;
        }
        int value = 0;
        for (int i = prefix.Length; i < path.Length; i++)
        {
            char c = path[i];
            if (c < '0' || c > '9')
                return false;
            value = value * 10 + (c - '0');
        }
        if (value < 1 || value > MaxTasks)
            return false;
        id = value;
        return true;
    }

    /// <summary>
    /// Name from the request body: JSON {"name":"..."} or form name=....
    /// ('+' decodes to a space in the form style.)
    /// </summary>
    private static string ExtractTaskName(string reqBody)
    {
        if (reqBody == null)
            return null;
        int key = IndexOfStr(reqBody, "\"name\"", 0);
        if (key >= 0)
        {
            int colon = WebService.IndexOfChar2(reqBody, ':', key + 6);
            if (colon >= 0)
            {
                int q1 = WebService.IndexOfChar2(reqBody, '"', colon + 1);
                int q2 = q1 >= 0 ? WebService.IndexOfChar2(reqBody, '"', q1 + 1) : -1;
                if (q2 > q1)
                    return reqBody.Substring(q1 + 1, q2 - q1 - 1);
            }
        }
        int eq = IndexOfStr(reqBody, "name=", 0);
        if (eq >= 0)
        {
            int start = eq + 5;
            int end = WebService.IndexOfChar2(reqBody, '&', start);
            if (end < 0)
                end = reqBody.Length;
            var chars = new char[end - start];
            for (int i = start; i < end; i++)
                chars[i - start] = reqBody[i] == '+' ? ' ' : reqBody[i];
            return new string(chars);
        }
        return null;
    }

    /// <summary>Value of one key in a query string, or null.</summary>
    private static string QueryValue(string query, string key)
    {
        if (query.Length == 0)
            return null;
        string needle = key + "=";
        int start = 0;
        while (start <= query.Length)
        {
            int amp = WebService.IndexOfChar2(query, '&', start);
            int end = amp < 0 ? query.Length : amp;
            if (end - start >= needle.Length)
            {
                bool match = true;
                for (int i = 0; i < needle.Length; i++)
                {
                    if (query[start + i] != needle[i])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    var chars = new char[end - start - needle.Length];
                    for (int i = start + needle.Length; i < end; i++)
                        chars[i - start - needle.Length] = query[i] == '+' ? ' ' : query[i];
                    return new string(chars);
                }
            }
            if (amp < 0)
                break;
            start = amp + 1;
        }
        return null;
    }

    // ====================================================================
    // HTTP I/O helpers
    // ====================================================================

    /// <summary>Build the response for one request and write it out.</summary>
    private static unsafe void Respond(SampleConnection conn, int status, string statusText,
        string contentType, string body)
    {
        if (body == null)
            body = "";
        string head =
            "HTTP/1.1 " + WebService.IntToStr(status) + " " + statusText + "\r\n" +
            "Content-Type: " + contentType + "\r\n" +
            "Content-Length: " + WebService.IntToStr(body.Length) + "\r\n" +
            "Server: NeutrinoOS-SampleApi\r\n" +
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
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i];
            char cb = b[i];
            if (ca >= 'A' && ca <= 'Z')
                ca = (char)(ca + 32);
            if (cb >= 'A' && cb <= 'Z')
                cb = (char)(cb + 32);
            if (ca != cb)
                return false;
        }
        return true;
    }

    /// <summary>Escape a string for embedding inside JSON quotes.</summary>
    private static string JsonEscape(string s)
    {
        if (s == null || s.Length == 0)
            return "";
        var chars = new char[s.Length * 2];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' || c == '\\')
            {
                chars[n++] = '\\';
                chars[n++] = c;
            }
            else if (c == '\r' || c == '\n' || c == '\t')
            {
                chars[n++] = ' ';
            }
            else
            {
                chars[n++] = c;
            }
        }
        var result = new char[n];
        for (int i = 0; i < n; i++)
            result[i] = chars[i];
        return new string(result);
    }
}
