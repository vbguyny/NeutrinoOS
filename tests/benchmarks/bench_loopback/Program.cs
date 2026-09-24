// NeutrinoOS Phase 7 benchmark: TCP throughput over the kernel loopback
// (Phase 7 feature: frames addressed to 127.0.0.0/8 are delivered back
// into the stack by NetworkPump instead of going to the NIC).
//
// A TcpServer listens on a port; a TcpSocket client connects to
// 127.0.0.1 and streams 4 MB in 1400-byte chunks; the server drains
// the stream; elapsed time comes from Stopwatch.
using System;
using System.Diagnostics;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;

namespace NeutrinoOS.Benchmarks.Loopback;

/// <summary>Loopback TCP throughput benchmark (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; prints loopback TCP MB/s.</summary>
    public static int Main(string[] args)
    {
        var eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.WriteLine("[bench] loopback: no eth0 stack (start with a NIC)");
            return 1;
        }
        var stack = eth.Stack;

        ushort port = 7901;
        var server = new TcpServer(stack, port, true);
        if (!server.Start())
        {
            Console.WriteLine("[bench] loopback: bind failed on port " + port);
            return 1;
        }

        var client = new TcpSocket(stack);
        if (!client.Connect(0x7F000001, port))
        {
            Console.WriteLine("[bench] loopback: connect failed");
            server.Stop();
            return 1;
        }

        var accepted = server.Accept(10000);
        if (accepted == null)
        {
            Console.WriteLine("[bench] loopback: accept timeout");
            server.Stop();
            return 1;
        }

        const int Chunk = 1400;
        const int TotalBytes = 4 * 1024 * 1024;
        // Keep outstanding bytes below the receiver's advertised window
        // (8192 bytes) and drain the receive side in the same loop - the
        // socket Send path returns short/0 when the send window is full.
        const int Pace = 7000;

        // Per-packet trace output would throttle the measurement to the
        // 115200-baud serial console; silence it for the bulk transfer.
        stack.Quiet = true;

        Console.WriteLine("[bench] loopback TCP: transferring " + (TotalBytes / 1024) + " KB...");

        byte* sbuf = stackalloc byte[Chunk];
        for (int i = 0; i < Chunk; i++)
            sbuf[i] = (byte)i;

        byte* rbuf = stackalloc byte[Chunk];
        int sent = 0;
        int received = 0;
        int idle = 0;

        Stopwatch sw = Stopwatch.StartNew();
        while (sent < TotalBytes && idle < 20)
        {
            int outstanding = sent - received;
            if (outstanding < Pace)
            {
                int n = client.Send(sbuf, Chunk);
                if (n > 0)
                {
                    sent += n;
                    continue;
                }
            }
            int r = accepted.ReceiveWait(rbuf, Chunk, 500);
            if (r > 0)
            {
                received += r;
                idle = 0;
            }
            else
            {
                idle++;
            }
        }

        // Drain the bytes still in flight so the byte count is complete.
        idle = 0;
        while (received < sent && idle < 20)
        {
            int r = accepted.ReceiveWait(rbuf, Chunk, 500);
            if (r > 0)
            {
                received += r;
                idle = 0;
            }
            else
            {
                idle++;
            }
        }
        long ms = sw.ElapsedMilliseconds;
        stack.Quiet = false;

        long kb = received / 1024;
        long kbps = ms > 0 ? kb * 1000 / ms : 0;
        long mbps100 = ms > 0 ? kb * 100 / ms : 0;
        Console.WriteLine("[bench] loopback TCP: sent=" + sent + " recv=" + received
            + " in " + ms + " ms -> " + (mbps100 / 100) + "." + (mbps100 % 100 < 10 ? "0" : "") + (mbps100 % 100)
            + " MB/s (" + kbps + " KB/s)");
        return received == TotalBytes ? 0 : 1;
    }
}
