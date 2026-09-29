// NeutrinoOS Phase 5 utility: wget - download a file via HTTP/1.1
//
// usage: wget [-O file] url
//   Minimal HTTP/1.1 GET client built on the DDK TcpSocket + the kernel
//   network pump. http:// and https:// URLs are supported; HTTPS uses
//   the DDK TLS 1.3 client (the certificate chain is NOT verified in
//   this phase - see src/ddk/Tls/Tls13Client.cs). Without -O the body
//   is printed to stdout.

using System;
using System.IO;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;
using ProtonOS.DDK.Network.Stack;
using ProtonOS.DDK.Tls;

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
                    "  Download an http:// or https:// URL via HTTP/1.1 (GET).",
                    "  -O file   save the response body to file (default: stdout)",
                    "  HTTPS uses TLS 1.3 (certificate not verified in this phase).");
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
        bool https;
        if (!Http.ParseUrl(url, out host, out port, out path, out https, out string schemeError))
            return Util.Fail("wget", schemeError);

        // Resolve + connect.
        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (!Http.EnsureDevice(eth, "wget"))
            return 0;   // environmental - reported, not an error

        var stack = eth.Stack;
        uint ip = ResolveHost(host, stack);
        if (ip == 0)
            return Util.Fail("wget", host + ": unknown host");

        // Connect (shared helper: ARP retry + transmits the queued SYN).
        TcpSocket sock = Http.Connect(ip, port, stack, 5000);
        if (sock == null)
            return Util.Fail("wget", "connection to " + host + ":" + FormatInt(port) + " timed out");

        // HTTPS: try TLS 1.3 first, then fall back to TLS 1.2 on a
        // fresh connection (several servers reset 1.3-only
        // ClientHellos but serve 1.2 fine). The certificate chain is
        // not verified in this phase - the Finished records still
        // authenticate the key schedule.
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
                    return Util.Fail("wget", "reconnect for TLS 1.2 failed");
                tls.Tls12 = new Tls12Client(sock, stack, host);
                if (!tls.Tls12.Handshake(12000))
                {
                    string reason12 = tls.Tls12.LastError;
                    return Util.Fail("wget", "TLS handshake failed (1.3: "
                        + (reason13 == null ? "unknown" : reason13) + "; 1.2: "
                        + (reason12 == null ? "unknown" : reason12) + ")");
                }
            }
            Console.Error.WriteLine("wget: note: TLS ok (certificate not verified)");
        }

        // Build + send request.
        string request = "GET " + path + " HTTP/1.1\r\n"
            + "Host: " + host + "\r\n"
            + "User-Agent: NeutrinoOS-wget/0.5\r\n"
            + "Accept: */*\r\n"
            + "Connection: close\r\n\r\n";
        byte[] requestBytes = Http.AsciiBytes(request);
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
                    return Util.Fail("wget", "failed to send request");
            }
            // The stack queues segments; the caller transmits them.
            NetworkPump.FlushTx(stack);
        }

        // Read the response.
        var sb = new System.Text.StringBuilder();
        if (tls != null)
        {
            byte[] tbuf = new byte[1460];
            ulong tstart = Timer.GetUptimeMilliseconds();
            while (Timer.GetUptimeMilliseconds() - tstart < 15000)
            {
                int n = tls.ReadApp(tbuf, 0, 1460);
                if (n > 0)
                {
                    for (int i = 0; i < n; i++)
                        sb.Append(tbuf[i] < 128 ? (char)tbuf[i] : '?');
                }
                else if (n < 0)
                {
                    break;
                }
            }
            tls.CloseGraceful();
        }
        else
        {
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
        }

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

    /// <summary>
    /// Hostname resolution in its own small method: the Tier-0 JIT has a
    /// history of miscompiling call sequences built inline in very large
    /// frames, which made every hostname fail with "unknown host" from
    /// wget's Main while the identical call from the small dns utility
    /// worked.
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
    }

    private static string FormatInt(int value)
        => value.ToString();
}
