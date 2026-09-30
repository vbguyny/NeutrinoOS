// NeutrinoOS Phase 5 utility: curl - HTTP client
//
// usage: curl [-o file] [-d data] url
//   Minimal HTTP/1.1 client on the DDK TcpSocket: GET by default, POST
//   with -d (application/x-www-form-urlencoded). http:// and https://
//   URLs are supported; HTTPS uses the DDK TLS 1.3 client (the
//   certificate chain is not verified in this phase).

using System;
using System.IO;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Sockets;
using NeutrinoOS.DDK.Network.Stack;
using NeutrinoOS.DDK.Tls;

namespace NeutrinoOS.Utility.Curl;

/// <summary>The curl utility (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; returns 1 on any failure.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        string outFile = null;
        string postData = null;
        string url = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: curl [-o file] [-d data] url",
                    "  Fetch an http:// or https:// URL (GET, or POST with -d data).",
                    "  -o file   save the response body to file (default: stdout)",
                    "  -d data   POST data (application/x-www-form-urlencoded)");
            }
            if (a == "-o")
            {
                if (i + 1 >= args.Length)
                    return Util.Fail("curl", "-o requires a file name");
                outFile = args[++i];
            }
            else if (a == "-d")
            {
                if (i + 1 >= args.Length)
                    return Util.Fail("curl", "-d requires data");
                postData = args[++i];
            }
            else if (url == null)
            {
                url = a;
            }
            else
            {
                return Util.Fail("curl", "usage: curl [-o file] [-d data] url");
            }
        }

        if (url == null)
            return Util.Fail("curl", "usage: curl [-o file] [-d data] url");

        string host;
        int port;
        string path;
        bool https;
        if (!Http.ParseUrl(url, out host, out port, out path, out https, out string schemeError))
            return Util.Fail("curl", schemeError);

        uint ip = Http.ParseIP(host);
        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (!Http.EnsureDevice(eth, "curl"))
            return 0;

        var stack = eth.Stack;
        if (ip == 0)
        {
            ip = ResolveHost(host, stack);
            if (ip == 0)
                return Util.Fail("curl", host + ": unknown host");
        }

        TcpSocket sock = Http.Connect(ip, port, stack, 5000);
        if (sock == null)
            return Util.Fail("curl", "connection to " + host + ":" + port.ToString() + " failed");

        // HTTPS: try TLS 1.3 first, then fall back to TLS 1.2 on a
        // fresh connection (several servers reset 1.3-only
        // ClientHellos but serve 1.2 fine). The certificate chain is
        // not verified in this phase.
        Https tls = null;
        if (https)
        {
            tls = new Https();
            tls.Tls13 = new Tls13Client(sock, stack, host);
            if (!tls.Tls13.Handshake(8000))
            {
                string reason13 = tls.Tls13.LastError;
                tls.Tls13 = null;
                sock.Close();
                sock = Http.Connect(ip, port, stack, 5000);
                if (sock == null)
                    return Util.Fail("curl", "reconnect for TLS 1.2 failed");
                tls.Tls12 = new Tls12Client(sock, stack, host);
                if (!tls.Tls12.Handshake(12000))
                {
                    string reason12 = tls.Tls12.LastError;
                    return Util.Fail("curl", "TLS handshake failed (1.3: "
                        + (reason13 == null ? "unknown" : reason13) + "; 1.2: "
                        + (reason12 == null ? "unknown" : reason12) + ")");
                }
            }
            Console.Error.WriteLine("curl: note: TLS ok (certificate not verified)");
        }

        // Build the request.
        var requestBuilder = new System.Text.StringBuilder();
        if (postData == null)
        {
            requestBuilder.Append("GET ");
        }
        else
        {
            requestBuilder.Append("POST ");
        }
        requestBuilder.Append(path);
        requestBuilder.Append(" HTTP/1.1\r\nHost: ");
        requestBuilder.Append(host);
        requestBuilder.Append("\r\nUser-Agent: NeutrinoOS-curl/0.5\r\nAccept: */*\r\n");
        if (postData != null)
        {
            byte[] postBytes = Http.AsciiBytes(postData);
            requestBuilder.Append("Content-Type: application/x-www-form-urlencoded\r\nContent-Length: ");
            requestBuilder.Append(postBytes.Length);
            requestBuilder.Append("\r\n");
        }
        requestBuilder.Append("Connection: close\r\n\r\n");
        if (postData != null)
            requestBuilder.Append(postData);

        byte[] requestBytes = Http.AsciiBytes(requestBuilder.ToString());
        if (tls != null)
        {
            tls.WriteApp(requestBytes, 0, requestBytes.Length);
        }
        else
        {
            fixed (byte* p = requestBytes)
            {
                int sent = sock.Send(p, requestBytes.Length);
                if (sent != requestBytes.Length)
                    return Util.Fail("curl", "failed to send request");
            }
            // The stack queues segments; the caller transmits them.
            NetworkPump.FlushTx(stack);
        }

        string all;
        if (tls != null)
        {
            all = Http.ReadResponseTls(tls, 15000);
            tls.CloseGraceful();
        }
        else
        {
            all = Http.ReadResponse(sock, stack, 15000);
            sock.Close();
        }

        if (all.Length == 0)
            return Util.Fail("curl", "no response received");

        string status = Http.StatusLine(all);
        string body = Http.BodyOf(all);
        int bodyLen = Http.CountBytes(body);

        if (outFile != null)
        {
            File.WriteAllText(outFile, body);
            Console.Write("curl: ");
            Console.Write(status);
            Console.Write(", saved ");
            Console.Write(bodyLen);
            Console.Write(" bytes to ");
            Console.WriteLine(outFile);
        }
        else
        {
            Console.Write(body);
            if (bodyLen > 0 && body[body.Length - 1] != '\n')
                Console.WriteLine();
            Console.Write("curl: ");
            Console.Write(status);
            Console.Write(", ");
            Console.Write(bodyLen);
            Console.WriteLine(" bytes to stdout");
        }
        return 0;
    }
    /// <summary>
    /// Hostname resolution in its own small method: the Tier-0 JIT has a
    /// history of miscompiling call sequences built inline in very large
    /// frames (see wget's identical helper).
    /// </summary>
    private static uint ResolveHost(string host, NetworkStack stack)
    {
        uint ip = Http.ParseIP(host);
        if (ip != 0)
            return ip;
        var resolver = new DnsResolver(stack);
        return resolver.Resolve(host, 8000,
            new DnsResolver.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
            new DnsResolver.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter));
    }}
