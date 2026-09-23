// ProtonOS DDK - minimal packet filter (Phase 6)
//
// A per-port / per-source allow-deny list for inbound TCP connections,
// configured through /etc/firewall.conf. This is deliberately simple
// (no stateful tracking; see docs/PHASE6-ACCEPTANCE.md):
//
//   # /etc/firewall.conf
//   deny ip 10.0.2.99        # block a source address
//   deny tcp 22              # block SSH
//   allow ip 10.0.2.2        # allow the QEMU host again
//   deny all                 # default-deny remainder
//   allow all                # default-allow (default when no rules)
//
// Rules are evaluated in order; the first matching rule decides.
// The filter is consulted by TcpListener.HandleIncomingSyn before a
// SYN-ACK is sent, so denied sources never establish a connection.

using System;
using System.IO;
using ProtonOS.DDK.Util;

namespace ProtonOS.DDK.Network;

/// <summary>Minimal inbound TCP filter (see file header).</summary>
public static class Firewall
{
    private const string ConfigPath = "/etc/firewall.conf";
    private const int MaxRules = 32;

    private const int KindAllowIp = 0;
    private const int KindDenyIp = 1;
    private const int KindAllowTcp = 2;
    private const int KindDenyTcp = 3;
    private const int KindAllowAll = 4;
    private const int KindDenyAll = 5;

    private static readonly int[] _kind = new int[MaxRules];
    private static readonly uint[] _ip = new uint[MaxRules];
    private static readonly ushort[] _port = new ushort[MaxRules];
    private static int _count;
    private static bool _loaded;

    /// <summary>Number of loaded rules (0 = no filter configured).</summary>
    public static int RuleCount
    {
        get
        {
            EnsureLoaded();
            return _count;
        }
    }

    /// <summary>
    /// True when an inbound TCP connection from <paramref name="srcIP"/>
    /// to <paramref name="dstPort"/> is allowed.
    /// </summary>
    public static bool AllowInbound(uint srcIP, ushort dstPort)
    {
        EnsureLoaded();
        for (int i = 0; i < _count; i++)
        {
            int kind = _kind[i];
            if (kind == KindAllowAll)
                return true;
            if (kind == KindDenyAll)
                return false;
            if (kind == KindAllowIp && _ip[i] == srcIP)
                return true;
            if (kind == KindDenyIp && _ip[i] == srcIP)
                return false;
            if (kind == KindAllowTcp && _port[i] == dstPort)
                return true;
            if (kind == KindDenyTcp && _port[i] == dstPort)
                return false;
        }
        return true;   // default allow
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        if (!File.Exists(ConfigPath))
            return;
        try
        {
            string[] lines = TextLines.Split(File.ReadAllText(ConfigPath));
            for (int i = 0; i < lines.Length && _count < MaxRules; i++)
            {
                ParseRule(lines[i]);
            }
            if (_count > 0)
            {
                Console.Write("[firewall] loaded ");
                Console.Write(IntStr(_count));
                Console.WriteLine(" rule(s) from /etc/firewall.conf");
            }
        }
        catch (Exception)
        {
        }
    }

    private static void ParseRule(string line)
    {
        string[] parts = SplitSpaces(line);
        if (parts.Length < 2)
            return;
        string verb = Lower(parts[0]);
        string what = Lower(parts[1]);

        if (StrEq(verb, "allow") && StrEq(what, "all"))
        {
            _kind[_count++] = KindAllowAll;
        }
        else if (StrEq(verb, "deny") && StrEq(what, "all"))
        {
            _kind[_count++] = KindDenyAll;
        }
        else if (StrEq(what, "tcp") && parts.Length >= 3)
        {
            int port = ParseInt(parts[2]);
            if (port <= 0 || port > 65535)
                return;
            _kind[_count] = StrEq(verb, "deny") ? KindDenyTcp : KindAllowTcp;
            _port[_count] = (ushort)port;
            _count++;
        }
        else if (StrEq(what, "ip") && parts.Length >= 3)
        {
            uint ip = ParseIPv4(parts[2]);
            _kind[_count] = StrEq(verb, "deny") ? KindDenyIp : KindAllowIp;
            _ip[_count] = ip;
            _count++;
        }
    }

    // ==================== helpers (JIT-world safe) ====================

    private static string[] SplitSpaces(string line)
    {
        // Strip comments after '#'.
        int hash = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '#')
            {
                hash = i;
                break;
            }
        }
        string text = hash >= 0 ? line.Substring(0, hash) : line;

        var parts = new string[8];
        int count = 0;
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool sep = i == text.Length || text[i] == ' ' || text[i] == '\t' || text[i] == '\r';
            if (sep)
            {
                if (start >= 0 && count < parts.Length)
                {
                    parts[count++] = text.Substring(start, i - start);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }
        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = parts[i];
        return result;
    }

    private static string Lower(string s)
    {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            chars[i] = (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
        }
        return new string(chars);
    }

    private static bool StrEq(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    private static int ParseInt(string s)
    {
        int value = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                break;
            value = value * 10 + (c - '0');
        }
        return value;
    }

    private static uint ParseIPv4(string s)
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

    private static string IntStr(int value)
    {
        if (value == 0)
            return "0";
        var digits = new char[12];
        int n = 0;
        while (value > 0)
        {
            digits[n++] = (char)('0' + (value % 10));
            value /= 10;
        }
        var result = new char[n];
        for (int i = 0; i < n; i++)
            result[i] = digits[n - 1 - i];
        return new string(result);
    }
}
