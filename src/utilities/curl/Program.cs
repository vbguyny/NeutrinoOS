// NeutrinoOS Phase 5 utility: curl - HTTP client
//
// usage: curl [-o file] [-d data] url
//   Minimal HTTP/1.1 client on the DDK TcpSocket: GET by default, POST
//   with -d (application/x-www-form-urlencoded). Only http:// URLs are
//   supported (no TLS in Phase 5 - documented limitation).

using System;
using System.IO;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;
using ProtonOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Curl;

/// <summary>The curl utility (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; returns 1 on any failure.</summary>
    public static int Main(string[] args)
    {
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
                    "  Fetch an http:// URL (GET, or POST with -d data).",
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
        if (!Http.ParseUrl(url, out host, out port, out path, out string schemeError))
            return Util.Fail("curl", schemeError);

        uint ip = Http.ParseIP(host);
        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (!Http.EnsureDevice(eth, "curl"))
            return 0;

        var stack = eth.Stack;
        if (ip == 0)
        {
            var resolver = new DnsResolver(stack);
            ip = resolver.Resolve(host, 5000,
                new DnsResolver.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
                new DnsResolver.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter));
            if (ip == 0)
                return Util.Fail("curl", host + ": unknown host");
        }

        TcpSocket sock = Http.Connect(ip, port, stack, 5000);
        if (sock == null)
            return Util.Fail("curl", "connection to " + host + ":" + port.ToString() + " failed");

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
        fixed (byte* p = requestBytes)
        {
            int sent = sock.Send(p, requestBytes.Length);
            if (sent != requestBytes.Length)
                return Util.Fail("curl", "failed to send request");
        }
        // The stack queues segments; the caller transmits them.
        NetworkPump.FlushTx(stack);

        string all = Http.ReadResponse(sock, stack, 15000);
        sock.Close();

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
}
