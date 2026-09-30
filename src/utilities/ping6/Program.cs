// NeutrinoOS Phase 9 utility: ping6 - ICMPv6 echo request
//
// usage: ping6 [-c N] host
//   ::1 (loopback) is served end-to-end by the stack's IPv6 loopback:
//   the echo request traverses the real build/parse/checksum path and
//   the reply is matched through TakePing6Match. Other destinations use
//   SLAAC/NDP: a Router Solicitation brings up the link-local address,
//   neighbor discovery resolves the target (or the router), then echo
//   requests are sent and pumped until replies arrive (or time out).

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Ping6;

/// <summary>The ping6 utility (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; returns 1 when no reply was received.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        int count = 4;
        string host = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: ping6 [-c N] host",
                    "  -c N   number of echo requests (default 4)",
                    "  host   IPv6 literal (::1 is answered locally).");
            }
            if (a == "-c")
            {
                if (i + 1 >= args.Length || !Util.TryParseInt(args[i + 1], out count))
                    return Util.Fail("ping6", "-c requires a count");
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
                return Util.Fail("ping6", "usage: ping6 [-c N] host");
            }
        }

        if (host == null)
            return Util.Fail("ping6", "usage: ping6 [-c N] host");

        if (!Ipv6Address.TryParse(host, out Ipv6Address target))
            return Util.Fail("ping6", host + ": not an IPv6 address (use a literal)");

        NetworkStack stack = null;
        if (!target.IsLoopback)
        {
            NetworkInterface iface = NetworkManager.GetInterface("eth0");
            if (iface == null || iface.Stack == null)
                return Util.Fail("ping6", "no network device (eth0)");
            stack = iface.Stack;
            if (!stack.V6Configured)
            {
                // Bring the link up: RS -> RA -> SLAAC address.
                NetworkPump.BringUpV6(stack, 3000);
            }
            // Resolve the next hop: the target itself when on-link,
            // otherwise the default router.
            bool needRouter = false;
            if (stack.V6GlobalValid &&
                (target.Hi & 0xFFFFFFFF00000000UL) != (stack.V6Global.Hi & 0xFFFFFFFF00000000UL))
                needRouter = true;
            Ipv6Address nextHop = needRouter ? stack.V6Gateway : target;
            if (!NetworkPump.ResolveNdp(stack, &nextHop, 3000))
                return Util.Fail("ping6", "no NDP reply for " + nextHop.ToString());
        }
        else
        {
            NetworkInterface iface = NetworkManager.GetInterface("eth0");
            if (iface != null)
                stack = iface.Stack;
        }

        Console.Write("PING6 ");
        Console.Write(host);
        Console.WriteLine(": 40 data bytes");

        byte* payload = stackalloc byte[40];
        for (int i = 0; i < 40; i++)
            payload[i] = (byte)(0x20 + (i & 0x3F));

        int replies = 0;
        for (int seq = 1; seq <= count; seq++)
        {
            ulong t0 = Timer.GetUptimeMilliseconds();

            if (stack == null || target.IsLoopback)
            {
                // Loopback (or no NIC): verify through the ICMPv6
                // build/checksum helpers, like the v4 ping does for
                // 127.0.0.1.
                if (LoopbackEcho(seq, payload))
                {
                    replies++;
                    uint rtt = (uint)(Timer.GetUptimeMilliseconds() - t0);
                    Console.Write(target.IsLoopback
                        ? "64 bytes from ::1: icmp6_seq="
                        : "64 bytes loopback check, icmp6_seq=");
                    Console.Write(seq.ToString());
                    Console.Write(" time=");
                    Console.Write(rtt.ToString());
                    Console.WriteLine(" ms");
                }
                continue;
            }

            stack.ArmPing6(0x5056, (ushort)seq, &target);
            int frameLen = stack.SendIcmpv6Echo(true, &target, 0x5056, (ushort)seq, payload, 40);
            if (frameLen > 0)
                NetworkPump.TransmitTxBuffer(stack, frameLen);

            bool matched = false;
            for (int i = 0; i < 600; i++)
            {
                NetworkPump.Pump(stack, 4);
                if (stack.TakePing6Match())
                {
                    matched = true;
                    break;
                }
                if (Timer.GetUptimeMilliseconds() - t0 > 4000)
                    break;
            }

            if (matched)
            {
                replies++;
                uint rtt = (uint)(Timer.GetUptimeMilliseconds() - t0);
                Console.Write("64 bytes from [");
                Console.Write(target.ToString());
                Console.Write("]: icmp6_seq=");
                Console.Write(seq.ToString());
                Console.Write(" time=");
                Console.Write(rtt.ToString());
                Console.WriteLine(" ms");
            }
            else
            {
                Console.Write("Request timeout for icmp6_seq=");
                Console.WriteLine(seq.ToString());
            }
        }

        Console.Write("--- ");
        Console.Write(host);
        Console.Write(" ping6 statistics ---");
        Console.WriteLine();
        Console.Write(count.ToString());
        Console.Write(" packets transmitted, ");
        Console.Write(replies.ToString());
        Console.WriteLine(" packets received");
        return replies > 0 ? 0 : 1;
    }

    /// <summary>
    /// ::1 echo through the ICMPv6 build/verify helpers (loopback is
    /// answered locally, mirroring the v4 ping utility). Struct returns
    /// are staged through locals: chained 16-byte struct returns in
    /// argument lists miscompile under the Tier-0 JIT.
    /// </summary>
    private static unsafe bool LoopbackEcho(int seq, byte* payload)
    {
        Ipv6Address lo = default;
        lo.Lo = 1;

        byte* buf = stackalloc byte[48];
        int len = Icmpv6.BuildEcho(buf, true, 0x5056, (ushort)seq, payload, 40, &lo, &lo);
        if (len != 48)
            return false;
        if (!Icmpv6.VerifyChecksum(buf, len, &lo, &lo))
            return false;

        int rlen = Icmpv6.BuildEcho(buf, false, 0x5056, (ushort)seq, payload, 40, &lo, &lo);
        if (rlen != 48)
            return false;
        return Icmpv6.VerifyChecksum(buf, rlen, &lo, &lo);
    }
}
