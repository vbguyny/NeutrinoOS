// NeutrinoOS DDK - web host service (Phase 6)
//
// The Phase 6 fallback HTTP/1.1 + HTTPS server (Kestrel porting is
// deferred to Phase 7; see docs/PHASE6-WEB.md). Serves a small built-in
// site (/, /health, /time) plus static files from /var/www, over plain
// HTTP and - when a certificate is available - TLS 1.3.
//
// Like sshd, this is a cooperative service: `webhost` asks the kernel's
// ServiceRegistry to start it and the kernel calls Tick() from the shell
// idle hook. Certificates live in /etc/ssl/certs/neutrinoos.crt and
// /etc/ssl/private/neutrinoos.key (PEM); they are generated on first
// start (self-signed Ed25519).

using System;
using System.IO;
using NeutrinoOS.DDK.Crypto;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Sockets;
using NeutrinoOS.DDK.Network.Stack;
using NeutrinoOS.DDK.Tls;
using NeutrinoOS.DDK.Util;

namespace NeutrinoOS.DDK.Services;

/// <summary>The HTTP/HTTPS web host service (see file header).</summary>
public static class WebService
{
    private const string ConfigPath = "/etc/webhost.conf";
    private const string CertPath = "/etc/ssl/certs/neutrinoos.crt";
    private const string KeyPath = "/etc/ssl/private/neutrinoos.key";
    internal const string WwwRoot = "/var/www";
    private const int MaxConnections = 10;

    // Phase 7 security: per-source-IP limits (overridable via
    // /etc/webhost.conf for deployments and tests).
    internal static int MaxConnectionsPerIp = 4;
    internal static int MaxRequestsPerIpPerSecond = 30;
    private const int IpSlots = 8;

    private static readonly uint[] _ipTable = new uint[IpSlots];
    private static readonly ulong[] _ipWindowStart = new ulong[IpSlots];
    private static readonly int[] _ipWindowCount = new int[IpSlots];

    private static TcpServer _httpListener;
    private static TcpServer _httpsListener;
    private static NetworkStack _stack;
    private static readonly WebConnection[] _connections = new WebConnection[MaxConnections];
    private static byte[] _certDer;
    private static byte[] _keySeed;
    private static QuicServer _quic;
    private static ushort _httpPort = 80;
    private static ushort _httpsPort = 443;
    private static bool _active;

    /// <summary>True when the listeners are running.</summary>
    public static bool Active => _active;

    /// <summary>Start the service; 0 = ok (called by the kernel registry).</summary>
    public static int Start()
    {
        if (_active)
            return 0;

        var eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.WriteLine("[web] no eth0 interface - start QEMU/VBox with a NIC");
            return -30;
        }
        _stack = eth.Stack;

        LoadConfig();

        if (!LoadOrGenerateCertificate())
            return -31;

        _httpListener = new TcpServer(_stack, _httpPort, true);
        if (!_httpListener.Start())
        {
            Console.Write("[web] bind failed on port ");
            Console.WriteLine(IntToStr(_httpPort));
            _httpListener = null;
            return -32;
        }

        _httpsListener = new TcpServer(_stack, _httpsPort, true);
        if (!_httpsListener.Start())
        {
            Console.Write("[web] https bind failed on port ");
            Console.WriteLine(IntToStr(_httpsPort));
            Console.WriteLine("[web] continuing with HTTP only");
            _httpsListener = null;
        }

        _active = true;
        _quic = new QuicServer(_certDer, _keySeed);
        Console.Write("[web] listening on port ");
        Console.Write(IntToStr(_httpPort));
        if (_httpsListener != null)
        {
            Console.Write(" (http) and port ");
            Console.Write(IntToStr(_httpsPort));
            Console.Write(" (https, TLS 1.3, Ed25519 certificate)");
        }
        Console.WriteLine();
        Console.WriteLine("[web] HTTP/3 on UDP port 443 (QUIC v1)");
        return 0;
    }

    /// <summary>Stop the service (called by the kernel registry).</summary>
    public static void Stop()
    {
        if (!_active)
            return;
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i] != null)
            {
                _connections[i].Close();
                _connections[i] = null;
            }
        }
        if (_httpListener != null)
        {
            _httpListener.Stop();
            _httpListener = null;
        }
        if (_httpsListener != null)
        {
            _httpsListener.Stop();
            _httpsListener = null;
        }
        _active = false;
        Console.WriteLine("[web] stopped");
    }

    /// <summary>One bounded work slice (called from the kernel idle hook).</summary>
    public static void Tick()
    {
        if (!_active)
            return;

        NetworkPump.Pump(_stack, 4);

        AcceptNew(_httpListener, false);
        AcceptNew(_httpsListener, true);

        for (int i = 0; i < _connections.Length; i++)
        {
            var conn = _connections[i];
            if (conn == null)
                continue;
            conn.Tick();
            if (conn.Closed)
            {
                _connections[i] = null;
            }
        }

        // Always move queued responses to the wire: NetworkPump.Pump only
        // flushes when an RX frame arrives, and a server that is purely
        // answering (no inbound traffic this slice) would otherwise leave
        // its segments parked in the shared TX buffer.
        NetworkPump.FlushTx(_stack);

        // HTTP/3: drain UDP port 443 and answer via QUIC.
        if (_quic != null)
        {
            _quic.Pump(_stack);
            NetworkPump.FlushTx(_stack);
        }
    }

    private static void AcceptNew(TcpServer listener, bool tls)
    {
        if (listener == null)
            return;
        while (listener.Pending())
        {
            var sock = listener.Accept();
            if (sock == null)
                break;

            // Phase 7: per-IP connection cap.
            uint peer = sock.RemoteAddress;
            int sameIp = 0;
            for (int i = 0; i < _connections.Length; i++)
            {
                var existing = _connections[i];
                if (existing != null && existing.PeerIp == peer)
                    sameIp++;
            }
            if (sameIp >= MaxConnectionsPerIp)
            {
                sock.Close();
                continue;
            }

            int slot = -1;
            for (int i = 0; i < _connections.Length; i++)
            {
                if (_connections[i] == null)
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0)
            {
                sock.Close();
                continue;
            }
            var conn = new WebConnection(sock, tls ? _certDer : null, tls ? _keySeed : null);
            _connections[slot] = conn;
        }
    }

    /// <summary>
    /// Phase 7: per-IP request window (sliding 1-second bucket). False
    /// means the caller should answer 429 and back off.
    /// </summary>
    internal static bool AllowRequest(uint ip)
    {
        ulong now = Timer.GetUptimeMilliseconds();

        int slot = -1;
        int free = -1;
        for (int i = 0; i < IpSlots; i++)
        {
            if (_ipTable[i] == ip)
            {
                slot = i;
                break;
            }
            if (free < 0 && _ipTable[i] == 0)
                free = i;
        }
        if (slot < 0)
        {
            slot = free >= 0 ? free : 0;   // steal slot 0 when the table is full
            _ipTable[slot] = ip;
            _ipWindowStart[slot] = now;
            _ipWindowCount[slot] = 0;
        }

        if (now - _ipWindowStart[slot] >= 1000)
        {
            _ipWindowStart[slot] = now;
            _ipWindowCount[slot] = 0;
        }
        _ipWindowCount[slot]++;
        return _ipWindowCount[slot] <= MaxRequestsPerIpPerSecond;
    }

    // ==================== Configuration / certificate ====================

    /// <summary>
    /// Resolve a request path to a full response description. Shared by
    /// the HTTP/1.1, HTTP/2 and HTTP/3 front ends.
    /// </summary>
    internal static bool BuildRoute(string path, out int status, out string statusText,
        out string contentType, out string body)
    {
        status = 404;
        statusText = "Not Found";
        contentType = "text/plain";
        body = "404 not found\n";

        // Strip a query string.
        int q = IndexOfChar(path, '?');
        string clean = q >= 0 ? path.Substring(0, q) : path;

        if (StrEq(clean, "/") || StrEq(clean, "/index.html"))
        {
            status = 200;
            statusText = "OK";
            contentType = "text/html";
            body =
                "<!doctype html><html><head><title>NeutrinoOS</title></head><body>" +
                "<h1>NeutrinoOS web host</h1>" +
                "<p>Managed .NET host on bare metal. HTTP/1.1 + HTTP/2 + HTTP/3.</p>" +
                "<ul><li><a href=\"/health\">/health</a></li>" +
                "<li><a href=\"/time\">/time</a></li></ul></body></html>\n";
            return true;
        }
        if (StrEq(clean, "/health"))
        {
            status = 200;
            statusText = "OK";
            contentType = "application/json";
            body = "{\"status\":\"ok\"}\n";
            return true;
        }
        if (StrEq(clean, "/time"))
        {
            // Use the proven `date` utility through the kernel shell
            // bridge (the tiny formatter helpers misbehave when compiled
            // inside the service graph - see PHASE6-WEB notes).
            int rc;
            string dateOut = ShellBridge.Exec("date", out rc);
            int de = dateOut.Length;
            while (de > 0 && (dateOut[de - 1] == '\n' || dateOut[de - 1] == '\r' || dateOut[de - 1] == ' '))
                de--;
            var dc = new char[de];
            for (int i = 0; i < de; i++)
                dc[i] = dateOut[i] == ' ' ? 'T' : dateOut[i];
            string utc = new string(dc);
            var timeBuf = new char[128];
            int n = 0;
            n = AddStr(timeBuf, n, "{\"utc\":\"");
            n = AddStr(timeBuf, n, utc);
            n = AddStr(timeBuf, n, "\",\"uptime_s\":");
            n = AddStr(timeBuf, n, IntToStr((int)(Timer.GetUptimeMilliseconds() / 1000)));
            n = AddStr(timeBuf, n, "}\n");
            var jsonChars = new char[n];
            for (int i = 0; i < n; i++)
                jsonChars[i] = timeBuf[i];
            status = 200;
            statusText = "OK";
            contentType = "application/json";
            body = new string(jsonChars);
            return true;
        }

        // Static files under /var/www.
        if (HasDotDot(clean))
        {
            status = 400;
            statusText = "Bad Request";
            contentType = "text/plain";
            body = "bad path\n";
            return true;
        }
        string full = WwwRoot + clean;
        bool isDir = false;
        try
        {
            isDir = Directory.Exists(full);
        }
        catch (Exception)
        {
        }
        if (isDir)
            full = full + "/index.html";

        bool exists = false;
        string fileBody = null;
        try
        {
            exists = File.Exists(full);
            if (exists)
                fileBody = File.ReadAllText(full);
        }
        catch (Exception)
        {
            exists = false;
        }

        if (exists)
        {
            status = 200;
            statusText = "OK";
            contentType = ContentTypeFor(full);
            body = fileBody;
        }
        return true;
    }

    private static int AddStr(char[] target, int pos, string s)
    {
        for (int i = 0; i < s.Length; i++)
            target[pos + i] = s[i];
        return pos + s.Length;
    }

    private static bool HasDotDot(string path)
    {
        for (int i = 0; i + 1 < path.Length; i++)
        {
            if (path[i] == '.' && path[i + 1] == '.')
                return true;
        }
        return false;
    }

    private static string ContentTypeFor(string file)
    {
        if (EndsWith(file, ".html") || EndsWith(file, ".htm"))
            return "text/html";
        if (EndsWith(file, ".css"))
            return "text/css";
        if (EndsWith(file, ".js"))
            return "application/javascript";
        if (EndsWith(file, ".json"))
            return "application/json";
        if (EndsWith(file, ".txt") || EndsWith(file, ".log") || EndsWith(file, ".conf"))
            return "text/plain";
        if (EndsWith(file, ".svg"))
            return "image/svg+xml";
        return "application/octet-stream";
    }

    private static bool EndsWith(string s, string suffix)
    {
        if (s.Length < suffix.Length)
            return false;
        int offset = s.Length - suffix.Length;
        for (int i = 0; i < suffix.Length; i++)
        {
            if (s[offset + i] != suffix[i])
                return false;
        }
        return true;
    }

    private static void LoadConfig()
    {
        MaxConnectionsPerIp = 4;
        MaxRequestsPerIpPerSecond = 30;
        if (!File.Exists(ConfigPath))
            return;
        try
        {
            string[] lines = TextLines.Split(File.ReadAllText(ConfigPath));
            for (int i = 0; i < lines.Length; i++)
            {
                string line = TrimStr(lines[i]);
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int eq = IndexOfChar(line, '=');
                if (eq <= 0)
                    continue;
                string key = TrimStr(line.Substring(0, eq));
                string value = TrimStr(line.Substring(eq + 1, line.Length - eq - 1));
                if (StrEq(key, "Port"))
                    _httpPort = (ushort)ParseInt(value, 80);
                else if (StrEq(key, "HttpsPort"))
                    _httpsPort = (ushort)ParseInt(value, 443);
                else if (StrEq(key, "MaxConnectionsPerIp"))
                {
                    int v = ParseInt(value, 4);
                    if (v > 0)
                        MaxConnectionsPerIp = v;
                }
                else if (StrEq(key, "MaxRequestsPerIpPerSecond"))
                {
                    int v = ParseInt(value, 30);
                    if (v > 0)
                        MaxRequestsPerIpPerSecond = v;
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool LoadOrGenerateCertificate()
    {
        try
        {
            if (File.Exists(CertPath) && File.Exists(KeyPath))
            {
                byte[] der = X509.FromPem(File.ReadAllText(CertPath));
                byte[] keyDer = X509.FromPem(File.ReadAllText(KeyPath));
                byte[] seed = X509.PeelEd25519Seed(keyDer);
                if (der != null && seed != null)
                {
                    _certDer = der;
                    _keySeed = seed;
                    return true;
                }
                Console.WriteLine("[web] existing certificate unreadable - regenerating");
            }
        }
        catch (Exception)
        {
        }

        // First start: generate a self-signed Ed25519 certificate.
        byte[] newSeed = Csprng.GetBytes(32);
        var dns = new string[] { "neutrinoos", "localhost", "neutrinoos.local" };
        var ips = new string[] { "127.0.0.1" };
        byte[] cert = X509.BuildSelfSigned(newSeed, "NeutrinoOS", dns, ips);
        _certDer = cert;
        _keySeed = newSeed;
        try
        {
            if (!Directory.Exists("/etc/ssl"))
                Directory.CreateDirectory("/etc/ssl");
            if (!Directory.Exists("/etc/ssl/certs"))
                Directory.CreateDirectory("/etc/ssl/certs");
            if (!Directory.Exists("/etc/ssl/private"))
                Directory.CreateDirectory("/etc/ssl/private");
            File.WriteAllText(CertPath, X509.ToPem(cert, "CERTIFICATE"));
            File.WriteAllText(KeyPath, X509.ToPem(X509.BuildPkcs8Ed25519(newSeed), "PRIVATE KEY"));
            Console.WriteLine("[web] generated self-signed certificate (/etc/ssl/certs/neutrinoos.crt)");
        }
        catch (Exception)
        {
            Console.WriteLine("[web] warning: could not persist certificate to /etc/ssl");
        }
        return true;
    }

    // ==================== tiny string helpers (JIT-world safe) ====================

    internal static string TrimStr(string s)
    {
        int start = 0;
        int end = s.Length;
        while (start < end && (s[start] == ' ' || s[start] == '\t' || s[start] == '\r'))
            start++;
        while (end > start && (s[end - 1] == ' ' || s[end - 1] == '\t' || s[end - 1] == '\r'))
            end--;
        return s.Substring(start, end - start);
    }

    internal static int IndexOfChar(string s, char c)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == c)
                return i;
        }
        return -1;
    }

    internal static int IndexOfChar2(string s, char c, int start)
    {
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == c)
                return i;
        }
        return -1;
    }

    internal static bool StrEq(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    internal static int ParseInt(string s, int fallback)
    {
        if (s == null || s.Length == 0)
            return fallback;
        int value = 0;
        bool any = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                break;
            value = value * 10 + (c - '0');
            any = true;
        }
        return any ? value : fallback;
    }

    /// <summary>Decode base64url (RFC 4648 section 5, no padding required).</summary>
    internal static byte[] Base64UrlDecode(string s)
    {
        if (s == null || s.Length == 0)
            return null;
        var tmp = new byte[(s.Length * 3) / 4 + 3];
        int acc = 0;
        int bits = 0;
        int o = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            int v;
            if (c >= 'A' && c <= 'Z')
                v = c - 'A';
            else if (c >= 'a' && c <= 'z')
                v = c - 'a' + 26;
            else if (c >= '0' && c <= '9')
                v = c - '0' + 52;
            else if (c == '-')
                v = 62;
            else if (c == '_')
                v = 63;
            else if (c == '=')
                break;
            else
                continue;
            acc = (acc << 6) | v;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                tmp[o++] = (byte)(acc >> bits);
            }
        }
        var r = new byte[o];
        for (int i = 0; i < o; i++)
            r[i] = tmp[i];
        return r;
    }

    internal static string IntToStr(int value)
    {
        if (value == 0)
            return "0";
        bool neg = value < 0;
        if (neg)
            value = -value;
        var digits = new char[12];
        int n = 0;
        while (value > 0)
        {
            digits[n++] = (char)('0' + (value % 10));
            value /= 10;
        }
        if (neg)
            digits[n++] = '-';
        var result = new char[n];
        for (int i = 0; i < n; i++)
            result[i] = digits[n - 1 - i];
        return new string(result);
    }
}

/// <summary>One HTTP or HTTPS connection (TLS is driven internally).</summary>
public sealed unsafe class WebConnection
{
    private const ulong IdleTimeoutMs = 12000;

    // Phase 7 security: per-connection request rate limit. A keep-alive
    // client that exceeds this within a 1-second window gets a 429 and
    // the connection is closed (the client must back off).
    private const int RequestsPerSecond = 20;

    private readonly TcpSocket _sock;
    private readonly Tls13Connection _tls;
    private bool _tlsReady;
    private bool _closed;

    // Phase 9: HTTP/2 state (plain h2c or, later, ALPN-negotiated h2).
    private Http2Connection _h2;
    private bool _h2Checked;

    private readonly byte[] _in = new byte[12288];
    private int _inLen;
    private int _requests;
    private ulong _lastActivity;
    private ulong _rateWindowStart;
    private int _rateWindowCount;

    /// <summary>True when the connection has finished or errored.</summary>
    public bool Closed => _closed;

    /// <summary>Source IP of the peer (Phase 7 rate limiting).</summary>
    public uint PeerIp => _sock.RemoteAddress;

    /// <summary>Read from the active transport (TLS or plain); -1 when closed.</summary>
    internal int TransportRead(byte[] buf, int off, int len)
    {
        if (_tls != null && _tlsReady)
            return _tls.ReadApp(buf, off, len);
        int got;
        fixed (byte* p = buf)
        {
            got = _sock.Receive(p + off, len);
        }
        return got < 0 ? -1 : got;
    }

    /// <summary>Write to the active transport; false when it closed.</summary>
    internal bool TransportWrite(byte[] buf, int off, int len)
    {
        if (_tls != null && _tlsReady)
        {
            _tls.WriteApp(buf, off, len);
            return !_tls.Closed;
        }
        int sent = 0;
        while (sent < len)
        {
            int got;
            fixed (byte* p = buf)
            {
                got = _sock.Send(p + off + sent, len - sent);
            }
            if (got <= 0)
                return false;
            sent += got;
        }
        return true;
    }

    /// <summary>Hand the connection over to the HTTP/2 engine.</summary>
    private void StartH2()
    {
        _h2 = new Http2Connection(this);
        _h2.Seed(_in, 0, _inLen);
        _inLen = 0;
    }

    /// <summary>Creates a connection; tlsCert/tlsSeed null means plain HTTP.</summary>
    public WebConnection(TcpSocket sock, byte[] tlsCert, byte[] tlsSeed)
    {
        _sock = sock;
        if (tlsCert != null && tlsSeed != null)
            _tls = new Tls13Connection(sock, tlsCert, tlsSeed);
        else
            _tlsReady = true;
        _lastActivity = Timer.GetUptimeMilliseconds();
    }

    /// <summary>Closes the connection (TLS close_notify when possible).</summary>
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        if (_tls != null && _tls.Connected)
            _tls.CloseGraceful();
        else
            _sock.Close();
    }

    /// <summary>One bounded work slice.</summary>
    public void Tick()
    {
        if (_closed)
            return;

        ulong now = Timer.GetUptimeMilliseconds();
        if (now - _lastActivity > IdleTimeoutMs)
        {
            Close();
            return;
        }

        // TLS handshake first.
        if (_tls != null && !_tlsReady)
        {
            _tls.Step();
            if (_tls.Closed)
            {
                _closed = true;
                return;
            }
            if (_tls.Connected)
            {
                _tlsReady = true;
                // Phase 9: an ALPN result of "h2" switches to HTTP/2. The
                // client still sends the connection preface (RFC 9113 3.4),
                // so the normal Http2Connection handshake applies.
                if (WebService.StrEq(_tls.AlpnSelected, "h2"))
                {
                    _h2Checked = true;
                    StartH2();
                    _h2.Tick();
                    if (_h2.Closed)
                        _closed = true;
                    return;
                }
            }
            else
            {
                return;
            }
        }

        // Phase 9: once in HTTP/2 mode the engine owns the transport -
        // no plain reads may race it for bytes.
        if (_h2 != null)
        {
            _h2.Tick();
            if (_h2.Closed)
                _closed = true;
            return;
        }

        // Read available plaintext bytes.
        int n;
        if (_tls != null)
        {
            n = _tls.ReadApp(_in, _inLen, _in.Length - _inLen);
            if (n < 0)
            {
                _closed = true;
                return;
            }
            if (n > 0)
            {
                _inLen += n;
                _lastActivity = now;
            }
        }
        else
        {
            n = 0;
            while (_inLen < _in.Length)
            {
                int got;
                fixed (byte* p = _in)
                {
                    got = _sock.Receive(p + _inLen, _in.Length - _inLen);
                }
                if (got <= 0)
                    break;
                _inLen += got;
                n += got;
                _lastActivity = now;
                if (got < 1024)
                    break;
            }

            // Phase 7: reclaim the connection slot as soon as the peer
            // has closed and no partial request is pending. Without this,
            // bursts of short-lived HTTP/1.0-style clients pin table slots
            // until the 12s idle timeout and later connections are refused.
            if (_inLen == 0 && !_closed &&
                (_sock.State == TcpState.CloseWait || _sock.State == TcpState.Closed))
            {
                _closed = true;
                _sock.Close();
                return;
            }
        }

        // Phase 9: HTTP/2. A plain connection beginning with the h2c
        // client preface switches protocols (prior knowledge).
        if (_h2 == null && !_h2Checked && _tls == null)
        {
            if (_inLen >= 3 && _in[0] == (byte)'P' && _in[1] == (byte)'R' && _in[2] == (byte)'I')
            {
                if (_inLen < Http2Connection.ClientPreface.Length)
                    return;   // wait for the rest of the preface
                _h2Checked = true;
                StartH2();
                _h2.Tick();
                if (_h2.Closed)
                    _closed = true;
                return;
            }
        }

        ProcessRequests();
    }

    // ==================== HTTP ====================

    private void ProcessRequests()
    {
        while (!_closed)
        {
            int headerEnd = FindHeaderEnd();
            if (headerEnd < 0)
            {
                if (_inLen >= _in.Length)
                    Close();
                return;
            }

            // Parse the request line + headers (text up to headerEnd).
            var text = new char[headerEnd];
            for (int i = 0; i < headerEnd; i++)
                text[i] = (char)_in[i];
            string head = new string(text);

            string method;
            string path;
            string version;
            string keepAlive;
            int contentLength;
            string upgrade;
            string h2settings;
            if (!ParseRequest(head, out method, out path, out version,
                    out keepAlive, out contentLength, out upgrade, out h2settings))
            {
                SendSimple(400, "Bad Request", "text/plain", "bad request\n", false);
                Close();
                return;
            }

            int totalLen = headerEnd + 4 + contentLength;
            if (_inLen < totalLen)
                return;   // wait for the body

            // Phase 9: h2c upgrade (RFC 7540 3.2). Accept with 101, then
            // hand the connection to HTTP/2; the captured request is
            // answered on stream 1.
            if (_tls == null && StrEqLower(upgrade, "h2c"))
            {
                byte[] settings = WebService.Base64UrlDecode(h2settings);
                string sw = "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: h2c\r\n\r\n";
                var sb = new byte[sw.Length];
                for (int i = 0; i < sw.Length; i++)
                    sb[i] = (byte)sw[i];
                int sent = 0;
                while (sent < sb.Length)
                {
                    int got;
                    fixed (byte* p = sb)
                    {
                        got = _sock.Send(p + sent, sb.Length - sent);
                    }
                    if (got <= 0)
                        break;
                    sent += got;
                }
                if (sent != sb.Length)
                {
                    Close();
                    return;
                }
                _h2Checked = true;
                _h2 = new Http2Connection(this);
                _h2.BeginUpgrade(method, path, settings);
                if (totalLen < _inLen)
                    _h2.Seed(_in, totalLen, _inLen - totalLen);
                _inLen = 0;
                return;
            }

            bool headOnly = WebService.StrEq(method, "HEAD");
            bool isGet = WebService.StrEq(method, "GET");
            bool isPost = WebService.StrEq(method, "POST");
            if (!isGet && !isPost && !headOnly)
            {
                SendSimple(405, "Method Not Allowed", "text/plain", "method not allowed\n", headOnly);
                Consume(totalLen);
                MaybeClose(keepAlive);
                continue;
            }

            // Phase 7: request-rate limits - per connection and per source
            // IP (429 + close when exceeded).
            if (!RateLimitOk() || !WebService.AllowRequest(_sock.RemoteAddress))
            {
                SendSimple(429, "Too Many Requests", "text/plain", "rate limit exceeded\n", headOnly);
                Consume(totalLen);
                Close();
                return;
            }

            _requests++;
            SendRoute(path, headOnly);
            Consume(totalLen);
            MaybeClose(keepAlive);
        }
    }

    private bool RateLimitOk()
    {
        ulong now = Timer.GetUptimeMilliseconds();
        if (now - _rateWindowStart >= 1000)
        {
            _rateWindowStart = now;
            _rateWindowCount = 1;
            return true;
        }
        _rateWindowCount++;
        return _rateWindowCount <= RequestsPerSecond;
    }

    private void MaybeClose(string keepAlive)
    {
        if (WebService.StrEq(keepAlive, "close") || _requests >= 100)
        {
            _sock.Close();
            _closed = true;
            return;
        }
        _lastActivity = Timer.GetUptimeMilliseconds();
    }
    private int FindHeaderEnd()
    {
        for (int i = 0; i + 3 < _inLen; i++)
        {
            if (_in[i] == 13 && _in[i + 1] == 10 && _in[i + 2] == 13 && _in[i + 3] == 10)
                return i;
        }
        return -1;
    }

    private void Consume(int count)
    {
        for (int i = count; i < _inLen; i++)
            _in[i - count] = _in[i];
        _inLen -= count;
    }

    private bool ParseRequest(string head, out string method, out string path,
        out string version, out string keepAlive, out int contentLength,
        out string upgrade, out string h2settings)
    {
        method = null;
        path = null;
        version = "HTTP/1.0";
        keepAlive = "close";
        contentLength = 0;
        upgrade = "";
        h2settings = "";

        // First line: method SP path SP version CRLF.
        int lineEnd = IndexOfStr(head, "\r\n", 0);
        if (lineEnd < 0)
            return false;
        string line = head.Substring(0, lineEnd);
        int sp1 = WebService.IndexOfChar(line, ' ');
        if (sp1 <= 0)
            return false;
        int sp2 = WebService.IndexOfChar2(line, ' ', sp1 + 1);
        if (sp2 <= 0)
            return false;
        method = line.Substring(0, sp1);
        path = line.Substring(sp1 + 1, sp2 - sp1 - 1);
        version = WebService.TrimStr(line.Substring(sp2 + 1, line.Length - sp2 - 1));

        if (WebService.StrEq(version, "HTTP/1.1"))
            keepAlive = "keep-alive";

        // Header scan.
        int pos = lineEnd + 2;
        while (pos < head.Length)
        {
            int end = IndexOfStr(head, "\r\n", pos);
            if (end < 0)
                break;
            string h = head.Substring(pos, end - pos);
            if (h.Length == 0)
                break;
            int colon = WebService.IndexOfChar(h, ':');
            if (colon > 0)
            {
                string name = WebService.TrimStr(h.Substring(0, colon));
                string value = WebService.TrimStr(h.Substring(colon + 1, h.Length - colon - 1));
                if (NameEq(name, "connection"))
                {
                    if (StrEqLower(value, "close"))
                        keepAlive = "close";
                }
                else if (NameEq(name, "content-length"))
                {
                    contentLength = WebService.ParseInt(value, 0);
                    if (contentLength > 1_000_000)
                        contentLength = 0;
                }
                else if (NameEq(name, "upgrade"))
                {
                    upgrade = value;
                }
                else if (NameEq(name, "http2-settings"))
                {
                    h2settings = value;
                }
            }
            pos = end + 2;
        }
        return true;
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

    private static bool StrEqLower(string a, string b)
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

    // ==================== Routing ====================

    private void SendRoute(string path, bool headOnly)
    {
        int status;
        string statusText;
        string contentType;
        string body;
        WebService.BuildRoute(path, out status, out statusText, out contentType, out body);
        SendSimple(status, statusText, contentType, body, headOnly);
    }

    // ==================== Responses ====================

    private void SendSimple(int status, string statusText, string contentType,
        string body, bool headOnly)
    {
        int bodyLen = 0;
        for (int i = 0; i < body.Length; i++)
            bodyLen++;   // UTF-8 length equals char count for our ASCII payloads
        var sb = new char[512 + body.Length];
        int n = 0;
        n = Add(sb, n, "HTTP/1.1 ");
        n = Add(sb, n, WebService.IntToStr(status));
        n = Add(sb, n, " ");
        n = Add(sb, n, statusText);
        n = Add(sb, n, "\r\nServer: NeutrinoOS\r\nContent-Type: ");
        n = Add(sb, n, contentType);
        n = Add(sb, n, "\r\nContent-Length: ");
        n = Add(sb, n, WebService.IntToStr(bodyLen));
        n = Add(sb, n, "\r\nConnection: keep-alive\r\n\r\n");
        if (!headOnly)
        {
            for (int i = 0; i < body.Length; i++)
                sb[n++] = body[i];
        }

        var bytes = new byte[n];
        for (int i = 0; i < n; i++)
            bytes[i] = (byte)sb[i];

        if (_tls != null)
        {
            _tls.WriteApp(bytes, 0, bytes.Length);
        }
        else
        {
            int sent = 0;
            while (sent < bytes.Length)
            {
                int got;
                fixed (byte* p = bytes)
                {
                    got = _sock.Send(p + sent, bytes.Length - sent);
                }
                if (got <= 0)
                {
                    Close();
                    return;
                }
                sent += got;
            }
        }

        // Access log (one line per request).
        Console.Write("[web] ");
        Console.Write(WebService.IntToStr(status));
        Console.Write(" ");
        Console.Write(WebService.IntToStr(bodyLen));
        Console.WriteLine("B");
    }

    private static int Add(char[] target, int pos, string s)
    {
        for (int i = 0; i < s.Length; i++)
            target[pos + i] = s[i];
        return pos + s.Length;
    }
}
