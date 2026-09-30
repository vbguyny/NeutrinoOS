// NeutrinoOS Phase 5 utility: netstat - network connections and stats
//
// usage: netstat
//   Shows registered interfaces, active TCP connections (local/remote
//   endpoints + state from the DDK TCP connection table) and protocol
//   counters from the shared NetworkStack instance.

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Netstat;

/// <summary>The netstat utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: netstat [-s]",
                "  Show interfaces, active TCP connections and packet counters.",
                "  -s  extended statistics (byte counters)");
        }
        if (args.Length > 0 && !(args.Length == 1 && args[0] == "-s"))
            return Util.Fail("netstat", "usage: netstat [-s]");

        bool extended = args.Length == 1 && args[0] == "-s";

        Console.WriteLine("INTERFACES");
        var interfaces = NetworkManager.Interfaces;
        for (int i = 0; i < interfaces.Count; i++)
        {
            NetworkInterface iface = interfaces[i];
            Console.Write("  ");
            Console.Write(PadRight(iface.Name, 6));
            Console.Write(FormatIP(iface.IPAddress));
            Console.Write("  ");
            Console.Write(iface.Type == InterfaceType.Loopback ? "loopback" : "ethernet");
            Console.Write("  ");
            Console.WriteLine(iface.State == InterfaceState.Down ? "down" : "up");
        }

        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.WriteLine();
            Console.WriteLine("(no network device bound - start QEMU with a NIC for");
            Console.WriteLine(" full netstat output)");
            return 0;
        }

        var stack = eth.Stack;

        Console.WriteLine();
        Console.WriteLine("TCP CONNECTIONS");
        ulong tcpSent, tcpRecv;
        int active;
        stack.GetTcpStats(out tcpSent, out tcpRecv, out active);
        Console.WriteLine("  Local Address      Remote Address     State");
        int shown = 0;
        for (int i = 0; i < stack.TcpConnectionSlots; i++)
        {
            TcpConnection conn = stack.GetTcpConnection(i);
            if (conn == null)
                continue;

            Console.Write("  ");
            Console.Write(PadRight(FormatEP(conn.LocalEndpoint.IP, conn.LocalEndpoint.Port), 19));
            Console.Write(PadRight(FormatEP(conn.RemoteEndpoint.IP, conn.RemoteEndpoint.Port), 19));
            Console.WriteLine(StateName(conn.State));
            shown++;
        }
        if (shown == 0)
            Console.WriteLine("  (no active connections)");

        if (stack.V6Configured)
        {
            Console.WriteLine();
            Console.WriteLine("IPV6");
            Ipv6Address ll = stack.V6LinkLocal;
            Console.Write("  link-local ");
            Console.WriteLine(stack.V6LinkLocalIsValid ? ll.ToString() : "(none)");
            if (stack.V6GlobalValid)
            {
                Ipv6Address gl = stack.V6Global;
                Console.Write("  global     ");
                Console.WriteLine(gl.ToString());
            }
            if (stack.V6RouterSeen)
            {
                Ipv6Address gw = stack.V6Gateway;
                Console.Write("  router     ");
                Console.WriteLine(gw.ToString());
            }
            Ipv6Address d6 = stack.V6Dns;
            if (!d6.IsUnspecified)
            {
                Console.Write("  dns6       ");
                Console.WriteLine(d6.ToString());
            }
        }

        Console.WriteLine();
        Console.WriteLine("COUNTERS");
        Console.WriteLine("  TCP:  sent=" + tcpSent.ToString() + " segments, received=" + tcpRecv.ToString()
            + ", active connections=" + active);
        ulong icmpSent, icmpRecv;
        stack.GetIcmpStats(out icmpSent, out icmpRecv);
        Console.WriteLine("  ICMP: sent=" + icmpSent.ToString() + ", received=" + icmpRecv.ToString());
        ulong udpSent, udpRecv;
        stack.GetUdpStats(out udpSent, out udpRecv);
        Console.WriteLine("  UDP:  sent=" + udpSent.ToString() + ", received=" + udpRecv.ToString());
        // Phase 7 byte counters (shown with -s; cheap enough to always compute).
        ulong ipIn, ipOut, tcpIn, tcpOut;
        stack.GetByteStats(out ipIn, out ipOut, out tcpIn, out tcpOut);
        if (extended)
        {
            Console.WriteLine("  BYTES  IPv4 in=" + ipIn.ToString() + ", out=" + ipOut.ToString()
                + "   TCP payload in=" + tcpIn.ToString() + ", out=" + tcpOut.ToString());
            Console.WriteLine("         (/dev/netstats carries interface-level frame counters)");
            Console.WriteLine("  IPV6   bytes in=" + stack.Ip6BytesIn.ToString() + ", out=" + stack.Ip6BytesOut.ToString()
                + "  ICMPv6 sent=" + stack.Icmp6Sent.ToString() + ", received=" + stack.Icmp6Received.ToString());
            Console.WriteLine("         NDP sent=" + stack.NdpSent.ToString() + ", received=" + stack.NdpReceived.ToString()
                + "  UDP6 sent=" + stack.Udp6Sent.ToString() + ", received=" + stack.Udp6Received.ToString());
            Console.WriteLine("         TCP6 sent=" + stack.Tcp6Sent.ToString() + ", received=" + stack.Tcp6Received.ToString());
        }
        return 0;
    }

    private static string FormatEP(uint ip, ushort port)
        // Note: no int.Parse/ToString(port) formatting helpers needed -
        // direct append keeps this JIT-friendly.
        => FormatIP(ip) + ":" + ((int)port).ToString();

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

    private static string StateName(TcpState state)
    {
        switch (state)
        {
            case TcpState.Established: return "ESTABLISHED";
            case TcpState.SynSent: return "SYN_SENT";
            case TcpState.SynReceived: return "SYN_RECEIVED";
            case TcpState.FinWait1: return "FIN_WAIT_1";
            case TcpState.FinWait2: return "FIN_WAIT_2";
            case TcpState.CloseWait: return "CLOSE_WAIT";
            case TcpState.Closing: return "CLOSING";
            case TcpState.LastAck: return "LAST_ACK";
            case TcpState.TimeWait: return "TIME_WAIT";
            case TcpState.Listen: return "LISTEN";
            default: return "CLOSED";
        }
    }

    private static string PadRight(string s, int width)
    {
        if (s.Length >= width)
            return s;
        var sb = new System.Text.StringBuilder(s);
        for (int i = s.Length; i < width; i++)
            sb.Append(' ');
        return sb.ToString();
    }
}
