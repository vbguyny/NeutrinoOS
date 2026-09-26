// NeutrinoOS Phase 9 utility: dhcp6 - request a DHCPv6 lease
//
// usage: dhcp6
//   Runs the DDK DHCPv6 client (SOLICIT/ADVERTISE/REQUEST/REPLY) on
//   eth0's link-local address through the kernel network pump, then
//   applies the leased address to the interface. Requires the
//   virtio-net device. Note: QEMU's slirp answers DHCPv6 on its side;
//   SLAAC (Router Advertisement) is not emitted by slirp.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Dhcp6;

/// <summary>The dhcp6 utility (see file header).</summary>
public static unsafe class Program
{
    /// <summary>Entry point; returns 1 when a lease could not be obtained.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: dhcp6",
                "  Request a DHCPv6 lease on eth0 and apply it to the interface.");
        }
        if (args.Length > 0)
            return Util.Fail("dhcp6", "usage: dhcp6");

        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.WriteLine("dhcp6: no ethernet device (eth0) present");
            return 0;
        }

        if (!eth.Stack.V6Configured)
            eth.Stack.ConfigureV6LinkLocal();

        Console.WriteLine("dhcp6: soliciting a lease on eth0 ...");
        var client = new Dhcp6Client(eth.Stack);
        Dhcp6Lease lease;
        bool ok = client.Run(3000,
            new Dhcp6Client.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
            new Dhcp6Client.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter),
            out lease);

        if (ok && !(lease.Address.Hi == 0 && lease.Address.Lo == 0))
            return ReportLease(eth, &lease);

        // No stateful answer. Fall back to stateless DHCPv6: an
        // INFORMATION-REQUEST (RFC 3736) only asks for configuration
        // data (DNS servers); lightweight servers such as QEMU's slirp
        // implement exactly this mode and discard SOLICITs with IA
        // options, so the fallback is what makes DHCPv6 useful there.
        Console.WriteLine("dhcp6: no lease offer, trying stateless information-request ...");
        bool okInfo = client.RunInfoRequest(4000,
            new Dhcp6Client.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
            new Dhcp6Client.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter),
            out lease);

        if (!okInfo)
        {
            Console.Error.WriteLine("neutrinoos: dhcp6: no REPLY received (timeout)");
            return 1;
        }
        return ReportInfoRequest(eth, &lease);
    }

    /// <summary>
    /// Adopt + print the stateful lease. Kept out of Main with a minimal
    /// frame: mixing live object references and 16-byte struct locals in
    /// one crowded Tier-0 JIT frame aliases slots (observed as an object
    /// slot clobbered by an Ipv6Address value - GP fault).
    /// </summary>
    private static int ReportLease(NetworkInterface eth, Dhcp6Lease* lease)
    {
        Ipv6Address none = default;
        eth.Stack.ConfigureV6Static(&lease->Address, &none, &lease->Dns);
        Console.Write("eth0: DHCPv6 address ");
        Console.Write(Addr(&lease->Address));
        Console.Write(" (server ");
        Console.Write(Addr(&lease->ServerAddress));
        if (!(lease->Dns.Hi == 0 && lease->Dns.Lo == 0))
        {
            Console.Write(", dns ");
            Console.Write(Addr(&lease->Dns));
        }
        Console.WriteLine(")");
        return 0;
    }

    /// <summary>Adopt + print the stateless reply (see ReportLease).</summary>
    private static int ReportInfoRequest(NetworkInterface eth, Dhcp6Lease* lease)
    {
        if (!(lease->Dns.Hi == 0 && lease->Dns.Lo == 0))
            eth.Stack.SetV6Dns(&lease->Dns);
        Console.Write("eth0: DHCPv6 stateless: server ");
        Console.Write(Addr(&lease->ServerAddress));
        if (!(lease->Dns.Hi == 0 && lease->Dns.Lo == 0))
        {
            Console.Write(", dns6 ");
            Console.Write(Addr(&lease->Dns));
        }
        Console.WriteLine("");
        return 0;
    }

    /// <summary>
    /// Format an address held in an out-struct field. Calling
    /// ToString() straight on a 16-byte field of a large out-struct is
    /// miscompiled by the Tier-0 JIT (method-table load through the
    /// field value - GP fault), so stage through primitive stores into
    /// a fresh local first (both patterns are known-good).
    /// </summary>
    private static string Addr(Ipv6Address* addr)
    {
        Ipv6Address v = default;
        v.Hi = addr->Hi;
        v.Lo = addr->Lo;
        return v.ToString();
    }
}
