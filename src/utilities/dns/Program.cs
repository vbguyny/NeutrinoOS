// NeutrinoOS Phase 5 utility: dns - resolve a hostname
//
// usage: dns host
//   IPv4 literals pass through; other names are resolved through the
//   DDK DnsResolver against the configured DNS server (QEMU's slirp
//   DNS at 10.0.2.3 by default in NIC sessions). Requires the
//   virtio-net device.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Dns;

/// <summary>The dns utility (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; returns 1 when resolution fails.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: dns host",
                "  Resolve host to an IPv4 address (uses the configured DNS server).");
        }
        if (args.Length != 1)
            return Util.Fail("dns", "usage: dns host");

        string host = args[0];
        uint literal = ParseIP(host);
        if (literal != 0)
        {
            Console.WriteLine(host + " -> " + FormatIP(literal));
            return 0;
        }

        if (!NetworkPump.IsAvailable)
        {
            Console.Error.WriteLine("neutrinoos: dns: network device not available");
            Console.Error.WriteLine("  (start QEMU with -device virtio-net-pci; see docs/PHASE5-UTILITIES.md)");
            return 0;
        }

        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.Error.WriteLine("neutrinoos: dns: no configured ethernet interface");
            return 0;
        }

        var resolver = new DnsResolver(eth.Stack);
        uint ip = resolver.Resolve(host, 5000,
            new DnsResolver.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
            new DnsResolver.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter));

        if (ip == 0)
            return Util.Fail("dns", host + ": resolution failed (timeout or no DNS server)");

        Console.WriteLine(host + " -> " + FormatIP(ip));
        return 0;
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
