// NeutrinoOS Phase 9 utility: dns6 - resolve a hostname to an AAAA record
//
// usage: dns6 host
//   IPv6 literals pass through; other names are resolved through the
//   DDK DnsResolver (AAAA). When a v6 DNS server was learned via RDNSS
//   or DHCPv6 the query goes over IPv6; otherwise the configured IPv4
//   server answers the AAAA lookup (RFC 3596).

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Dns6;

/// <summary>The dns6 utility (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; returns 1 when resolution fails.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: dns6 host",
                "  Resolve host to an IPv6 address (AAAA record).");
        }
        if (args.Length != 1)
            return Util.Fail("dns6", "usage: dns6 host");

        string host = args[0];
        if (Ipv6Address.TryParse(host, out Ipv6Address literal))
        {
            Console.WriteLine(host + " -> " + literal.ToString());
            return 0;
        }

        if (!NetworkPump.IsAvailable)
        {
            Console.Error.WriteLine("neutrinoos: dns6: network device not available");
            return 1;
        }

        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.Error.WriteLine("neutrinoos: dns6: no configured ethernet interface");
            return 1;
        }

        // Bring IPv6 up so an RDNSS-provided DNS server can be used.
        if (!eth.Stack.V6Configured)
            NetworkPump.BringUpV6(eth.Stack, 3000);

        // The AAAA query may travel over IPv4 when no v6 DNS server was
        // learned; resolve the v4 server's MAC first (ARP needed).
        if (eth.Stack.V6Dns.IsUnspecified && eth.Stack.Config.DnsServer != 0)
            NetworkPump.ResolveArp(eth.Stack, eth.Stack.Config.DnsServer, 3000);

        // When the query goes over IPv6, the DNS server's link address
        // must be resolved first (NDP), same as a real host would.
        Ipv6Address dnsSrv = eth.Stack.V6Dns;
        if (!dnsSrv.IsUnspecified)
            NetworkPump.ResolveNdp(eth.Stack, &dnsSrv, 3000);

        var resolver = new DnsResolver(eth.Stack);
        Ipv6Address ip = resolver.ResolveV6(host, 5000,
            new DnsResolver.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
            new DnsResolver.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter));

        // Happy-Eyeballs style fallback: when the DNSv6 server does not
        // answer (some environments, incl. QEMU slirp without a host
        // IPv6 nameserver, never reply), retry the AAAA lookup over the
        // configured IPv4 DNS server (RFC 3596).
        if (ip.IsUnspecified)
        {
            Ipv6Address dns6Srv = eth.Stack.V6Dns;
            if (!dns6Srv.IsUnspecified && eth.Stack.Config.DnsServer != 0)
            {
                Console.WriteLine("dns6: no answer via DNSv6 server, retrying over IPv4 ...");
                eth.Stack.ClearV6Dns();
                NetworkPump.ResolveArp(eth.Stack, eth.Stack.Config.DnsServer, 3000);
                ip = resolver.ResolveV6(host, 5000,
                    new DnsResolver.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
                    new DnsResolver.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter));
            }
        }

        if (ip.IsUnspecified)
            return Util.Fail("dns6", host + ": AAAA resolution failed (timeout or no DNS server)");

        Console.WriteLine(host + " -> " + ip.ToString());
        return 0;
    }
}
