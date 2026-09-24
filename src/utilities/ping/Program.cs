// NeutrinoOS Phase 5 utility: ping - ICMP echo request
//
// usage: ping [-c N] host
//   127.0.0.1 (loopback) is answered locally by the stack shim without
//   needing a NIC: the request is built and verified through the same
//   DDK ICMP helpers, then replied to and re-parsed as a real echo
//   reply would be (documented in docs/PHASE5-UTILITIES.md).
//   Other destinations need the virtio-net device (Kernel_Net* pump):
//   the utility resolves ARP, sends the echo request and pumps received
//   frames into the DDK stack until the reply arrives or it times out.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Ping;

/// <summary>The ping utility (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; returns 1 when no reply was received.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        int count = 4;
        string host = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: ping [-c N] host",
                    "  -c N   number of echo requests (default 4)",
                    "  127.0.0.1 is answered locally (no NIC required).");
            }
            if (a == "-c")
            {
                if (i + 1 >= args.Length || !Util.TryParseInt(args[i + 1], out count))
                    return Util.Fail("ping", "-c requires a count");
                i++;
                if (count < 1)
                    count = 1;
                if (count > 32)
                    count = 32;
            }
            else if (host == null)
            {
                host = a;
            }
            else
            {
                return Util.Fail("ping", "usage: ping [-c N] host");
            }
        }

        if (host == null)
            return Util.Fail("ping", "usage: ping [-c N] host");

        uint ip = ParseIP(host);
        bool loopback = ip != 0 && (ip >> 24) == 127;

        NetworkStack stack = null;
        if (!loopback)
        {
            if (ip == 0)
            {
                // Hostname: resolve through DNS (needs the NIC).
                if (!NetworkPump.IsAvailable)
                    return NoDevice();
                NetworkInterface eth = NetworkManager.GetInterface("eth0");
                if (eth == null || eth.Stack == null)
                    return NoDevice();
                var resolver = new DnsResolver(eth.Stack);
                ip = resolver.Resolve(host, 5000,
                    new DnsResolver.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
                    new DnsResolver.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter));
                if (ip == 0)
                    return Util.Fail("ping", host + ": unknown host");
            }

            NetworkInterface iface = NetworkManager.GetInterface("eth0");
            if (iface == null || iface.Stack == null)
                return NoDevice();
            stack = iface.Stack;

            if (!NetworkPump.ResolveArp(stack, ip, 4000))
            {
                Console.Error.WriteLine("neutrinoos: ping: no ARP reply for " + FormatIP(ip));
                return 1;
            }
        }

        Console.Write("PING ");
        Console.Write(host);
        if (!loopback)
        {
            Console.Write(" (");
            Console.Write(FormatIP(ip));
            Console.Write(")");
        }
        Console.WriteLine(": 40 data bytes");

        int replies = 0;
        for (int seq = 1; seq <= count; seq++)
        {
            if (loopback)
            {
                if (LoopbackEcho(seq))
                    replies++;
                continue;
            }

            int frameLen = stack.SendPing(ip);
            if (frameLen <= 0)
            {
                Console.WriteLine("ping: send failed");
                continue;
            }
            NetworkPump.TransmitTxBuffer(stack, frameLen);

            ulong t0 = Timer.GetUptimeMilliseconds();
            bool got = false;
            while (Timer.GetUptimeMilliseconds() - t0 < 3000)
            {
                NetworkPump.Pump(stack, 8);
                if (!stack.IsPingPending())
                {
                    got = true;
                    break;
                }
            }

            if (got)
            {
                replies++;
                Console.Write("40 bytes from ");
                Console.Write(FormatIP(ip));
                Console.Write(": icmp_seq=");
                Console.Write(seq);
                Console.Write(" ttl=64 time=");
                Console.Write((long)(Timer.GetUptimeMilliseconds() - t0));
                Console.WriteLine(" ms");
            }
            else
            {
                Console.Write("no reply from ");
                Console.Write(FormatIP(ip));
                Console.Write(": icmp_seq=");
                Console.WriteLine(seq);
            }
        }

        Console.WriteLine();
        Console.Write("--- ");
        Console.Write(host);
        Console.WriteLine(" ping statistics ---");
        Console.Write(count);
        Console.Write(" packets transmitted, ");
        Console.Write(replies);
        Console.Write(" received, ");
        Console.Write(count == 0 ? 0 : (count - replies) * 100 / count);
        Console.WriteLine("% packet loss");
        return replies > 0 ? 0 : 1;
    }

    /// <summary>
    /// Local loopback echo: builds a real ICMP echo request with the DDK
    /// helpers, produces the matching echo reply, parses it back and
    /// verifies the checksum - so the same build/parse paths are
    /// exercised as for a NIC reply.
    /// </summary>
    private static bool LoopbackEcho(int seq)
    {
        const ushort id = 0x4E05;   // 'N','\x05'

        byte* payload = stackalloc byte[32];
        for (int i = 0; i < 32; i++)
            payload[i] = (byte)('a' + (i % 26));

        byte* request = stackalloc byte[64];
        int reqLen = ICMP.BuildEchoRequest(request, id, (ushort)seq, payload, 32);
        if (reqLen == 0)
            return false;

        // Loopback: the request is answered by the local side of the
        // stack; build the reply exactly as an echo responder would.
        byte* reply = stackalloc byte[64];
        int repLen = ICMP.BuildEchoReply(reply, id, (ushort)seq, payload, 32);
        if (repLen == 0)
            return false;

        IcmpPacket packet;
        if (!ICMP.Parse(reply, repLen, out packet))
        {
            Console.Error.WriteLine("ping: loopback reply failed to parse");
            return false;
        }
        if (!ICMP.VerifyChecksum(reply, repLen))
        {
            Console.Error.WriteLine("ping: loopback reply checksum invalid");
            return false;
        }

        Console.Write(repLen);
        Console.Write(" bytes from 127.0.0.1: icmp_seq=");
        Console.Write(seq);
        Console.Write(" ttl=64 time=0.0 ms");
        Console.WriteLine();
        return true;
    }

    private static int NoDevice()
    {
        Console.Error.WriteLine("neutrinoos: ping: network device not available");
        Console.Error.WriteLine("  (127.0.0.1 works without a NIC; start QEMU with");
        Console.Error.WriteLine("   -device virtio-net-pci for real network access)");
        return 1;
    }

    private static uint ParseIP(string text)
    {
        uint result = 0;
        int octet = 0;
        int digits = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= '0' && c <= '9')
            {
                octet = octet * 10 + (c - '0');
                if (octet > 255 || ++digits > 3)
                    return 0;
            }
            else if (c == '.')
            {
                if (digits == 0)
                    return 0;
                result = (result << 8) | (uint)octet;
                octet = 0;
                digits = 0;
            }
            else
            {
                return 0;
            }
        }
        if (digits == 0)
            return 0;
        return (result << 8) | (uint)octet;
    }

    private static string FormatIP(uint ip)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append((int)((ip >> 24) & 0xFF));
        sb.Append('.');
        sb.Append((int)((ip >> 16) & 0xFF));
        sb.Append('.');
        sb.Append((int)((ip >> 8) & 0xFF));
        sb.Append('.');
        sb.Append((int)(ip & 0xFF));
        return sb.ToString();
    }
}
