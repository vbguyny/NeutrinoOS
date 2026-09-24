// NeutrinoOS Phase 5 utility: dhcp - request a DHCP lease
//
// usage: dhcp
//   Runs the DDK DHCP client (DISCOVER/OFFER/REQUEST/ACK) on eth0 via
//   the kernel network pump, then applies the lease to the interface.
//   Requires the virtio-net device; without one the utility reports it
//   and exits 0 (environmental condition, see docs/PHASE5-UTILITIES.md).

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Dhcp;

/// <summary>The dhcp utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a lease could not be obtained.</summary>
    public static unsafe int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: dhcp",
                "  Request a DHCP lease on eth0 and apply it to the interface.");
        }
        if (args.Length > 0)
            return Util.Fail("dhcp", "usage: dhcp");

        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.WriteLine("dhcp: no ethernet device (eth0) present");
            Console.WriteLine("  (start QEMU with -device virtio-net-pci; see docs/PHASE5-UTILITIES.md)");
            return 0;
        }

        Console.WriteLine("dhcp: requesting lease on eth0 ...");
        bool ok = NetworkManager.ConfigureWithDhcp(eth,
            new DhcpClient.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
            new DhcpClient.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter),
            10000);

        if (!ok)
        {
            Console.Error.WriteLine("neutrinoos: dhcp: no lease received (timeout)");
            return 1;
        }

        Console.Write("eth0: leased ");
        Console.Write(FormatIP(eth.IPAddress));
        Console.Write(" netmask ");
        Console.Write(FormatIP(eth.SubnetMask));
        Console.Write(" gateway ");
        Console.Write(FormatIP(eth.Gateway));
        Console.Write(" dns ");
        Console.WriteLine(FormatIP(eth.DnsServer));
        return 0;
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
