// NeutrinoOS Phase 6 utility: socktest - TCP server socket smoke test
//
// Usage: socktest [port] [timeoutSec]
//
// Binds a listening port on eth0, accepts one connection, sends a
// banner, echoes whatever the client sends back, then closes. Used by
// build/p6-sock-test.sh (QEMU user-net + hostfwd) to verify the Phase 6
// server-side socket layer (bind/listen/accept/send/receive/close).

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;

namespace NeutrinoOS.Utility.Socktest;

/// <summary>The socktest utility (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; sends and receives server-side TCP data.</summary>
    public static int Main(string[] args)
    {
        int port = 7777;
        int timeoutSec = 20;

        int p, t;
        if (args.Length >= 1 && Util.TryParseInt(args[0], out p) && p > 0 && p < 65536)
            port = p;
        if (args.Length >= 2 && Util.TryParseInt(args[1], out t) && t > 0)
            timeoutSec = t;

        var eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Util.Fail("socktest", "no eth0 interface (start QEMU with a virtio NIC)");
            return 1;
        }

        var server = new TcpServer(eth.Stack, (ushort)port, true);
        if (!server.Start())
        {
            Util.Fail("socktest", "bind failed on port " + port);
            return 1;
        }

        Console.Write("[socktest] listening on port ");
        Console.WriteLine(port);

        var sock = server.Accept(timeoutSec * 1000);
        if (sock == null)
        {
            Console.WriteLine("[socktest] accept timeout - FAIL");
            server.Stop();
            return 1;
        }

        Console.WriteLine("[socktest] accepted connection");

        byte* rbuf = stackalloc byte[512];
        byte* sbuf = stackalloc byte[600];

        int bannerLen = AsciiBytes("HELLO-FROM-NEUTRINOOS\n", sbuf);
        int sentBanner = sock.Send(sbuf, bannerLen);
        Console.Write("[socktest] sent banner, ");
        Console.Write(Util.PadLeft(sentBanner, 1));
        Console.WriteLine(" bytes");

        int n = sock.ReceiveWait(rbuf, 512, 5000);
        if (n <= 0)
        {
            Console.WriteLine("[socktest] receive timeout - FAIL");
            sock.Close();
            server.Stop();
            return 1;
        }

        Console.Write("[socktest] received ");
        Console.Write(Util.PadLeft(n, 1));
        Console.Write(" bytes: ");
        PrintBytes(rbuf, n);

        // Echo back: "ECHO:" + the received bytes.
        int pos = AsciiBytes("ECHO:", sbuf);
        for (int i = 0; i < n; i++)
            sbuf[pos + i] = rbuf[i];
        int echoed = sock.Send(sbuf, pos + n);
        Console.Write("[socktest] echoed ");
        Console.Write(Util.PadLeft(echoed, 1));
        Console.WriteLine(" bytes");

        sock.Close();
        server.Stop();

        Console.WriteLine("[socktest] PASS");
        return 0;
    }

    private static int AsciiBytes(string s, byte* buf)
    {
        for (int i = 0; i < s.Length; i++)
            buf[i] = (byte)s[i];
        return s.Length;
    }

    private static void PrintBytes(byte* data, int length)
    {
        for (int i = 0; i < length; i++)
        {
            byte b = data[i];
            if (b == '\r' || b == '\n')
                Console.Write(' ');
            else if (b >= 32 && b < 127)
                Console.Write((char)b);
            else
                Console.Write('.');
        }
        Console.WriteLine();
    }
}
