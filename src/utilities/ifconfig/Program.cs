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
using ProtonOS.DDK.Network;

namespace NeutrinoOS.Utility.Ifconfig;

/// <summary>The ifconfig utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
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
                "  'ifconfig eth0 static <ip> <mask> <gateway> [dns]' applies IP settings.");
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
        return Util.Fail("ifconfig", op + ": expected 'up', 'down' or 'static'");
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
