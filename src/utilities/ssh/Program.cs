// NeutrinoOS Phase 5 utility: ssh - SSH client (TCP reachability)
//
// usage: ssh [-p port] [user@]host
//
// Phase 5 scope (specs/phase-5.md): no SSH *server*; the SSH *client*
// may document its limitation. This utility implements the transport
// half: it resolves the host, opens a real TCP connection to the SSH
// port (22 by default) through the DDK stack and reports whether the
// server is reachable. The SSH protocol handshake (wolfSSH integration)
// is explicitly out of Phase 5 scope and is documented in
// docs/PHASE5-UTILITIES.md.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Network;

namespace NeutrinoOS.Utility.Ssh;

/// <summary>The ssh utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; 0 when the server is reachable, 1 otherwise.</summary>
    public static unsafe int Main(string[] args)
    {
        int port = 22;
        string target = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: ssh [-p port] [user@]host",
                    "  Opens a TCP connection to the SSH port and reports",
                    "  reachability. The SSH handshake is not implemented",
                    "  in Phase 5 (see docs/PHASE5-UTILITIES.md).");
            }
            if (a == "-p")
            {
                if (i + 1 >= args.Length || !Util.TryParseInt(args[i + 1], out port) || port <= 0 || port > 65535)
                    return Util.Fail("ssh", "-p requires a port number");
                i++;
            }
            else if (target == null)
            {
                target = a;
            }
            else
            {
                return Util.Fail("ssh", "usage: ssh [-p port] [user@]host");
            }
        }

        if (target == null)
            return Util.Fail("ssh", "usage: ssh [-p port] [user@]host");

        // Strip an optional user@ prefix (authentication is not reached
        // in Phase 5; the user name is reported for clarity).
        string user = null;
        string host = target;
        int at = -1;
        for (int i = 0; i < target.Length; i++)
        {
            if (target[i] == '@')
                at = i;
        }
        if (at >= 0)
        {
            user = target.Substring(0, at);
            host = target.Substring(at + 1);
        }

        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (!Http.EnsureDevice(eth, "ssh"))
            return 1;

        uint ip = Http.ParseIP(host);
        if (ip == 0)
        {
            var resolver = new ProtonOS.DDK.Network.Stack.DnsResolver(eth.Stack);
            ip = resolver.Resolve(host, 5000,
                new ProtonOS.DDK.Network.Stack.DnsResolver.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
                new ProtonOS.DDK.Network.Stack.DnsResolver.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter));
            if (ip == 0)
                return Util.Fail("ssh", host + ": unknown host");
        }

        Console.Write("ssh: connecting to ");
        if (user != null)
        {
            Console.Write(user);
            Console.Write('@');
        }
        Console.Write(host);
        Console.Write(':');
        Console.Write(port);
        Console.WriteLine(" ...");

        var sock = Http.Connect(ip, port, eth.Stack, 8000);
        if (sock == null)
            return Util.Fail("ssh", "could not connect to " + host + ":" + port + " (timeout or refused)");

        sock.Close();
        Console.Write("ssh: TCP connection to ");
        Console.Write(host);
        Console.Write(':');
        Console.Write(port);
        Console.WriteLine(" established - server is reachable");
        Console.WriteLine("ssh: the SSH protocol handshake is not implemented in Phase 5");
        Console.WriteLine("     (SSH client is documented as a limitation, see docs/PHASE5-UTILITIES.md)");
        return 0;
    }
}
