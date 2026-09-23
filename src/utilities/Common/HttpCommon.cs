// NeutrinoOS Phase 5 utilities - shared HTTP/TCP helpers
//
// Compiled into every utility together with UtilCommon (see the build
// script). The network utilities (wget, curl, ssh, ping, dns, dhcp,
// ifconfig, netstat) use these helpers; utilities that never touch the
// network carry the code but no runtime cost (it is only linked in when
// referenced).

using System;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;
using ProtonOS.DDK.Network.Stack;

namespace NeutrinoOS.Utils;

/// <summary>Shared HTTP/URL/TCP helpers for the Phase 5 utilities.</summary>
public static unsafe class Http
{
    /// <summary>Parses an http:// URL into host, port and path.</summary>
    public static bool ParseUrl(string url, out string host, out int port,
        out string path, out string error)
    {
        host = "";
        port = 80;
        path = "/";
        error = null;

        if (StartsWith(url, "https://"))
        {
            error = "https:// is not supported in Phase 5 (no TLS)";
            return false;
        }
        if (!StartsWith(url, "http://"))
        {
            error = "only http:// URLs are supported";
            return false;
        }

        int start = 7;
        int i = start;
        int colon = -1;
        while (i < url.Length && url[i] != '/')
        {
            if (url[i] == ':')
                colon = i;
            i++;
        }

        int hostEnd = colon >= 0 ? colon : i;
        if (hostEnd <= start)
        {
            error = "missing host in URL";
            return false;
        }
        host = url.Substring(start, hostEnd - start);

        if (colon >= 0)
        {
            port = 0;
            int digits = 0;
            for (int d = colon + 1; d < i; d++)
            {
                char c = url[d];
                if (c < '0' || c > '9')
                {
                    error = "invalid port in URL";
                    return false;
                }
                port = port * 10 + (c - '0');
                digits++;
            }
            if (digits == 0)
            {
                error = "missing port in URL";
                return false;
            }
        }

        if (i < url.Length)
            path = url.Substring(i);
        return true;
    }

    /// <summary>Parses a dotted-quad IPv4 literal (0 = not an IP).</summary>
    public static uint ParseIP(string text)
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

    /// <summary>ASCII-encodes a string (non-ASCII becomes '?').</summary>
    public static byte[] AsciiBytes(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bytes[i] = c < 128 ? (byte)c : (byte)'?';
        }
        return bytes;
    }

    /// <summary>First line (status line) of an HTTP response.</summary>
    public static string StatusLine(string response)
    {
        int end = 0;
        while (end < response.Length && response[end] != '\r' && response[end] != '\n')
            end++;
        return response.Substring(0, end);
    }

    /// <summary>Body part of an HTTP response (after the header blank line).</summary>
    public static string BodyOf(string response)
    {
        for (int i = 0; i + 3 < response.Length; i++)
        {
            if (response[i] == '\r' && response[i + 1] == '\n'
                && response[i + 2] == '\r' && response[i + 3] == '\n')
                return response.Substring(i + 4);
        }
        return "";
    }

    /// <summary>Byte count of a (ASCII-decoded) body string.</summary>
    public static int CountBytes(string body) => body.Length;

    /// <summary>
    /// Prints the standard "no network device" message and returns false
    /// unless eth0 with a DDK stack exists.
    /// </summary>
    public static bool EnsureDevice(NetworkInterface eth, string program)
    {
        if (eth != null && eth.Stack != null)
            return true;
        Console.Error.WriteLine("neutrinoos: " + program + ": network device not available");
        Console.Error.WriteLine("  (start QEMU with -device virtio-net-pci; see docs/PHASE5-UTILITIES.md)");
        return false;
    }

    /// <summary>
    /// Waits for a TCP socket to finish connecting, pumping frames.
    /// Returns true when established.
    /// </summary>
    public static bool WaitConnected(TcpSocket sock, NetworkStack stack, int timeoutMs)
    {
        ulong start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < (ulong)timeoutMs)
        {
            if (sock.Connected)
                return true;
            if (sock.State == TcpState.Closed)
                return false;
            NetworkPump.Pump(stack, 8);
        }
        return sock.Connected;
    }

    /// <summary>
    /// Full connect path: TCP connect (resolving ARP when required,
    /// transmitting the queued SYN) and wait for establishment.
    /// Returns null when the connection fails.
    /// </summary>
    public static TcpSocket Connect(uint ip, int port, NetworkStack stack, int timeoutMs)
    {
        var sock = new TcpSocket(stack);
        if (!sock.Connect(ip, (ushort)port))
        {
            // -2 from TcpConnect means ARP resolution is pending; resolve
            // and retry once.
            NetworkPump.ResolveArp(stack, ip, 3000);
            if (!sock.Connect(ip, (ushort)port))
                return null;
        }
        // TcpConnect only builds the SYN; the caller transmits it.
        NetworkPump.FlushTx(stack);
        if (!WaitConnected(sock, stack, timeoutMs))
        {
            sock.Close();
            NetworkPump.FlushTx(stack);
            return null;
        }
        return sock;
    }

    /// <summary>Reads an HTTP response body into a string (ASCII).</summary>
    public static string ReadResponse(TcpSocket sock, NetworkStack stack, int timeoutMs)
    {
        var sb = new System.Text.StringBuilder();
        byte* buf = stackalloc byte[1460];
        ulong start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < (ulong)timeoutMs)
        {
            NetworkPump.Pump(stack, 8);
            int n = sock.Receive(buf, 1460);
            if (n > 0)
            {
                for (int i = 0; i < n; i++)
                    sb.Append(buf[i] < 128 ? (char)buf[i] : '?');
            }
            else if (!sock.Connected && sock.Available == 0)
            {
                break;
            }
        }
        return sb.ToString();
    }

    private static bool StartsWith(string text, string prefix)
    {
        if (prefix.Length > text.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (text[i] != prefix[i])
                return false;
        }
        return true;
    }
}
