// NeutrinoOS Phase 5 utility: ifconfig - network interface configuration
//
// usage: ifconfig                              - show all interfaces
//        ifconfig eth0 up|down                 - change interface state
//        ifconfig eth0 static <ip> <mask> <gateway> [dns]
//                                              - apply a static configuration
//
// Reads the live DDK NetworkManager state (shared between the driver,
// kernel and utilities), so the eth0 entry registered by the virtio-net
// driver at boot appears here.

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Ifconfig;

/// <summary>The ifconfig utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static unsafe int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        bool showHelp = false;
        string name = null;
        string op = null;
        string p1 = null;
        string p2 = null;
        string p3 = null;
        string p4 = null;

        int positional = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                showHelp = true;
                break;
            }
            if (positional == 0)
                name = a;
            else if (positional == 1)
                op = a;
            else if (positional == 2)
                p1 = a;
            else if (positional == 3)
                p2 = a;
            else if (positional == 4)
                p3 = a;
            else if (positional == 5)
                p4 = a;
            else
                return Util.Fail("ifconfig", "usage: ifconfig [interface [up|down|static ...]]");
            positional++;
        }

        if (showHelp)
        {
            return Util.Help(
                "usage: ifconfig [interface [up|down|static ...]]",
                "  With no arguments, shows every network interface.",
                "  'ifconfig eth0 up|down' changes the interface state.",
                "  'ifconfig eth0 static <ip> <mask> <gateway> [dns]' applies IP settings.",
                "  'ifconfig eth0 static6 <addr> [gateway] [dns6]' applies an IPv6 address.");
        }

        if (name == null)
        {
            ShowAll();
            return 0;
        }

        if (op == null)
            return Util.Fail("ifconfig", "usage: ifconfig [interface [up|down]]");

        NetworkInterface iface = NetworkManager.GetInterface(name);
        if (iface == null)
            return Util.Fail("ifconfig", name + ": no such interface");

        if (op == "up")
        {
            iface.Up();
            Console.Write(name);
            Console.WriteLine(": interface up");
            // Phase 9: bring the IPv6 side up too (link-local + RS/RA).
            if (iface.Stack != null && iface.Type != InterfaceType.Loopback)
            {
                if (!iface.Stack.V6Configured || !iface.Stack.V6RouterSeen)
                {
                    NeutrinoOS.DDK.Network.NetworkPump.BringUpV6(iface.Stack, 10000);
                    Console.Write(name);
                    Console.WriteLine(iface.Stack.V6RouterSeen
                        ? ": IPv6 up (router advertisement received)"
                        : ": IPv6 link-local up");
                }
            }
            return 0;
        }
        if (op == "down")
        {
            iface.Down();
            Console.Write(name);
            Console.WriteLine(": interface down");
            return 0;
        }
        if (op == "static")
        {
            if (p1 == null || p2 == null || p3 == null)
                return Util.Fail("ifconfig", "usage: ifconfig <iface> static <ip> <mask> <gateway> [dns]");
            uint ip = ParseIP(p1);
            uint mask = ParseIP(p2);
            uint gw = ParseIP(p3);
            uint dns = p4 != null ? ParseIP(p4) : 0;
            if (!NetworkManager.ConfigureStatic(iface, ip, mask, gw, dns))
                return Util.Fail("ifconfig", name + ": static configuration failed");
            Console.Write(name);
            Console.WriteLine(": static configuration applied");
            return 0;
        }
        if (op == "static6")
        {
            if (p1 == null || iface.Stack == null)
                return Util.Fail("ifconfig", "usage: ifconfig <iface> static6 <addr> [gateway] [dns6]");
            if (!Ipv6Address.TryParse(p1, out Ipv6Address addr))
                return Util.Fail("ifconfig", p1 + ": invalid IPv6 address");
            Ipv6Address gw6 = default;
            Ipv6Address dns6 = default;
            if (p2 != null && !Ipv6Address.TryParse(p2, out gw6))
                return Util.Fail("ifconfig", p2 + ": invalid IPv6 gateway");
            if (p3 != null && !Ipv6Address.TryParse(p3, out dns6))
                return Util.Fail("ifconfig", p3 + ": invalid IPv6 DNS server");
            iface.Stack.ConfigureV6Static(&addr, &gw6, &dns6);
            Console.Write(name);
            Console.WriteLine(": IPv6 address applied");
            return 0;
        }
        return Util.Fail("ifconfig", op + ": expected 'up', 'down', 'static' or 'static6'");
    }

    /// <summary>Parses a dotted-quad IPv4 address into host byte order."</summary>
    public static uint ParseIP(string s)
    {
        uint value = 0;
        int part = 0;
        int cur = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || s[i] == '.')
            {
                value = (value << 8) | (uint)(cur & 0xFF);
                part++;
                cur = 0;
                if (part == 4)
                    break;
            }
            else if (s[i] >= '0' && s[i] <= '9')
            {
                cur = cur * 10 + (s[i] - '0');
            }
        }
        return value;
    }

    private static void ShowAll()
    {
        var interfaces = NetworkManager.Interfaces;
        for (int i = 0; i < interfaces.Count; i++)
        {
            NetworkInterface iface = interfaces[i];
            Console.Write(iface.Name);
            Console.Write(": flags=");
            bool loopback = iface.Type == InterfaceType.Loopback;
            Console.Write(loopback ? "LOOPBACK" : (iface.State == InterfaceState.Down ? "DOWN" : "UP"));
            Console.WriteLine("  mtu 1500");

            if (!loopback && iface.State == InterfaceState.Down && iface.IPAddress == 0)
            {
                Console.WriteLine("    (no address configured)");
            }
            else
            {
                Console.Write("    inet ");
                Console.Write(FormatIP(iface.IPAddress));
                Console.Write("  netmask ");
                Console.WriteLine(loopback ? "255.0.0.0" : FormatIP(iface.SubnetMask));
            }

            // Phase 9: IPv6 addresses (link-local and global).
            if (!loopback && iface.Stack != null && iface.Stack.V6Configured)
            {
                if (iface.Stack.V6LinkLocalIsValid)
                {
                    Console.Write("    inet6 ");
                    Console.Write(iface.Stack.V6LinkLocal.ToString());
                    Console.WriteLine("  prefixlen 64  scope link");
                }
                if (iface.Stack.V6GlobalValid)
                {
                    Console.Write("    inet6 ");
                    Console.Write(iface.Stack.V6Global.ToString());
                    Console.WriteLine("  prefixlen 64");
                }
                if (!iface.Stack.V6Gateway.IsUnspecified)
                {
                    Console.Write("    gateway6 ");
                    Console.WriteLine(iface.Stack.V6Gateway.ToString());
                }
                if (!iface.Stack.V6Dns.IsUnspecified)
                {
                    Console.Write("    dns6 ");
                    Console.WriteLine(iface.Stack.V6Dns.ToString());
                }
            }

            if (!loopback)
            {
                if (iface.Gateway != 0)
                {
                    Console.Write("    gateway ");
                    Console.WriteLine(FormatIP(iface.Gateway));
                }
                if (iface.DnsServer != 0)
                {
                    Console.Write("    dns ");
                    Console.Write(FormatIP(iface.DnsServer));
                    if (iface.DnsServer2 != 0)
                    {
                        Console.Write(", ");
                        Console.Write(FormatIP(iface.DnsServer2));
                    }
                    Console.WriteLine();
                }

                Console.Write("    ether ");
                // Note: NetworkInterface.MacAddress returns a
                // ReadOnlySpan<byte> and korlib has no ReadOnlySpan, so
                // the span-based getter cannot be JIT-compiled. The
                // driver-registered interface exposes its MAC through
                // the stack's byte* instead.
                if (iface.Stack != null)
                {
                    unsafe
                    {
                        byte* m = iface.Stack.MacAddress;
                        if (m != null)
                        {
                            for (int b = 0; b < 6; b++)
                            {
                                if (b > 0)
                                    Console.Write(':');
                                AppendHex(m[b]);
                            }
                            Console.WriteLine();
                        }
                        else
                        {
                            Console.WriteLine("(unavailable)");
                        }
                    }
                }
                else
                {
                    Console.WriteLine("(unavailable)");
                }
            }
            Console.WriteLine();
        }
    }

    private static void AppendHex(byte value)
    {
        const string hex = "0123456789abcdef";
        Console.Write(hex[(value >> 4) & 0xF]);
        Console.Write(hex[value & 0xF]);
    }

    /// <summary>Formats a host-order IPv4 address.</summary>
    public static string FormatIP(uint ip)
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
