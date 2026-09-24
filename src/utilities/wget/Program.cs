// NeutrinoOS Phase 5 utility: wget - download a file via HTTP/1.1
//
// usage: wget [-O file] url
//   Minimal HTTP/1.1 GET client built on the DDK TcpSocket + the kernel
//   network pump. Only http:// URLs are supported (HTTPS needs TLS,
//   which is out of Phase 5 scope - documented limitation). Without -O
//   the body is printed to stdout.

using System;
using System.IO;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;
using ProtonOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Wget;

/// <summary>The wget utility (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; returns 1 on any failure.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        string outFile = null;
        string url = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: wget [-O file] url",
                    "  Download an http:// URL via HTTP/1.1 (GET).",
                    "  -O file   save the response body to file (default: stdout)");
            }
            if (a == "-O")
            {
                if (i + 1 >= args.Length)
                    return Util.Fail("wget", "-O requires a file name");
                outFile = args[++i];
            }
            else if (url == null)
            {
                url = a;
            }
            else
            {
                return Util.Fail("wget", "usage: wget [-O file] url");
            }
        }

        if (url == null)
            return Util.Fail("wget", "usage: wget [-O file] url");

        string host;
        int port;
        string path;
        if (!Http.ParseUrl(url, out host, out port, out path, out string schemeError))
            return Util.Fail("wget", schemeError);

        // Resolve + connect.
        uint ip = Http.ParseIP(host);
        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (!Http.EnsureDevice(eth, "wget"))
            return 0;   // environmental - reported, not an error

        var stack = eth.Stack;
        if (ip == 0)
        {
            var resolver = new DnsResolver(stack);
            ip = resolver.Resolve(host, 5000,
                new DnsResolver.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
                new DnsResolver.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter));
            if (ip == 0)
                return Util.Fail("wget", host + ": unknown host");
        }

        // Connect (shared helper: ARP retry + transmits the queued SYN).
        TcpSocket sock = Http.Connect(ip, port, stack, 5000);
        if (sock == null)
            return Util.Fail("wget", "connection to " + host + ":" + FormatInt(port) + " timed out");

        // Build + send request.
        string request = "GET " + path + " HTTP/1.1\r\n"
            + "Host: " + host + "\r\n"
            + "User-Agent: NeutrinoOS-wget/0.5\r\n"
            + "Accept: */*\r\n"
            + "Connection: close\r\n\r\n";
        byte[] requestBytes = Http.AsciiBytes(request);
        fixed (byte* p = requestBytes)
        {
            int sent = sock.Send(p, requestBytes.Length);
            if (sent != requestBytes.Length)
                return Util.Fail("wget", "failed to send request");
        }
        // The stack queues segments; the caller transmits them.
        NetworkPump.FlushTx(stack);

        // Read the response.
        var sb = new System.Text.StringBuilder();
        byte* buf = stackalloc byte[1460];
        ulong start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < 15000)
        {
            NetworkPump.Pump(stack, 8);
            int n = sock.Receive(buf, 1460);
            if (n > 0)
            {
                for (int i = 0; i < n; i++)
                    sb.Append(buf[i] < 128 ? (char)buf[i] : '?');
            }
            else if (!sock.Connected && sock.Available == 0)
            {
                break;
            }
        }
        sock.Close();

        string all = sb.ToString();
        if (all.Length == 0)
            return Util.Fail("wget", "no response received");

        string status = Http.StatusLine(all);
        string body = Http.BodyOf(all);
        int bodyLen = Http.CountBytes(body);

        if (outFile != null)
        {
            File.WriteAllText(outFile, body);
            Console.Write("wget: ");
            Console.Write(status);
            Console.Write(", saved ");
            Console.Write(bodyLen);
            Console.Write(" bytes to ");
            Console.WriteLine(outFile);
        }
        else
        {
            Console.Write(body);
            if (bodyLen > 0 && (body[body.Length - 1] != '\n'))
                Console.WriteLine();
            Console.Write("wget: ");
            Console.Write(status);
            Console.Write(", ");
            Console.Write(bodyLen);
            Console.WriteLine(" bytes to stdout");
        }
        return 0;
    }

    private static string FormatInt(int value)
        => value.ToString();
}
