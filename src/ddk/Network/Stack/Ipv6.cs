// NeutrinoOS DDK - IPv6 Layer (L3)
// Address type, 40-byte header build/parse, extension header walking,
// pseudo-header checksums and utility helpers (RFC 8200).

using System;
using System.Runtime.InteropServices;

namespace NeutrinoOS.DDK.Network.Stack;

/// <summary>
/// IPv6 next-header (protocol) numbers used by this stack.
/// </summary>
public static class Ipv6NextHeader
{
    public const byte HopByHop = 0;
    public const byte Tcp = 6;
    public const byte Udp = 17;
    public const byte Routing = 43;
    public const byte Fragment = 44;
    public const byte Icmpv6 = 58;
    public const byte NoNext = 59;
    public const byte DestinationOptions = 60;
}

/// <summary>
/// A 128-bit IPv6 address split into two 64-bit words (Hi = bytes 0-7,
/// Lo = bytes 8-15). Kept as plain ulongs so comparisons and copies are
/// register-only and cheap; helpers convert to/from wire bytes.
/// </summary>
public unsafe struct Ipv6Address : IEquatable<Ipv6Address>
{
    /// <summary>Upper 64 bits (bytes 0..7 of the wire format).</summary>
    public ulong Hi;

    /// <summary>Lower 64 bits (bytes 8..15 of the wire format).</summary>
    public ulong Lo;

    /// <summary>The unspecified address (::).</summary>
    public static Ipv6Address Any => default;

    /// <summary>The loopback address (::1).</summary>
    public static Ipv6Address Loopback
    {
        get
        {
            Ipv6Address a = default;
            a.Lo = 1;
            return a;
        }
    }

    /// <summary>All-nodes link-local multicast (ff02::1).</summary>
    public static Ipv6Address AllNodesMulticast
    {
        get
        {
            Ipv6Address a = default;
            a.Hi = 0xFF02000000000000UL;
            a.Lo = 1;
            return a;
        }
    }

    /// <summary>All-routers link-local multicast (ff02::2).</summary>
    public static Ipv6Address AllRoutersMulticast
    {
        get
        {
            Ipv6Address a = default;
            a.Hi = 0xFF02000000000000UL;
            a.Lo = 2;
            return a;
        }
    }

    /// <summary>All DHCPv6 servers/relay-agents (ff02::1:2).</summary>
    public static Ipv6Address DhcpServersMulticast
    {
        get
        {
            Ipv6Address a = default;
            a.Hi = 0xFF02000000000000UL;
            a.Lo = 0x00010002;
            return a;
        }
    }

    /// <summary>True when the address is the unspecified address (::).</summary>
    public bool IsUnspecified => Hi == 0 && Lo == 0;

    /// <summary>True when the address is loopback (::1).</summary>
    public bool IsLoopback => Hi == 0 && Lo == 1;

    /// <summary>True when the address is multicast (ff00::/8).</summary>
    public bool IsMulticast => (Hi >> 56) == 0xFF;

    /// <summary>True when the address is link-local unicast (fe80::/10).</summary>
    public bool IsLinkLocal => ((Hi >> 48) & 0xFFC0UL) == 0xFE80UL;

    /// <summary>True when the address is a v4-mapped address (::ffff:a.b.c.d).</summary>
    public bool IsV4Mapped => Hi == 0 && (Lo >> 32) == 0xFFFFUL;

    /// <summary>Extract the embedded IPv4 address of a v4-mapped address.</summary>
    public uint V4MappedToUint => (uint)(Lo & 0xFFFFFFFFUL);

    /// <summary>Build the v4-mapped form (::ffff:a.b.c.d) of an IPv4 address.</summary>
    public static Ipv6Address FromV4(uint ip)
    {
        Ipv6Address a = default;
        a.Lo = 0xFFFF00000000UL | (ip & 0xFFFFFFFFUL);
        return a;
    }

    /// <summary>True when the address is a solicited-node multicast (ff02::1:ffxx:xxxx).</summary>
    public bool IsSolicitedNode
    {
        get
        {
            // ff02::1:ff00:0000/104
            return Hi == 0xFF02000000000000UL && (Lo >> 24) == 0x000001FFUL;
        }
    }

    /// <summary>Build the solicited-node multicast address for this unicast address.</summary>
    public Ipv6Address SolicitedNode()
    {
        Ipv6Address a = default;
        a.Hi = 0xFF02000000000000UL;
        // ff02::1:ffXX:XXXX - the low 24 bits of the target address are
        // copied into the last 24 bits (constant part: 0000:0001:ff00::/104).
        a.Lo = 0x00000001FF000000UL | (Lo & 0x00FFFFFFUL);
        return a;
    }

    /// <summary>Read an address from wire bytes (network order).</summary>
    public static Ipv6Address FromBytes(byte* data)
    {
        Ipv6Address a = default;
        ulong hi = 0;
        ulong lo = 0;
        for (int i = 0; i < 8; i++)
            hi = (hi << 8) | data[i];
        for (int i = 8; i < 16; i++)
            lo = (lo << 8) | data[i];
        a.Hi = hi;
        a.Lo = lo;
        return a;
    }

    /// <summary>Write the address to wire bytes (network order).</summary>
    public void WriteTo(byte* data)
    {
        ulong hi = Hi;
        ulong lo = Lo;
        for (int i = 7; i >= 0; i--)
        {
            data[i] = (byte)(hi & 0xFF);
            hi >>= 8;
        }
        for (int i = 15; i >= 8; i--)
        {
            data[i] = (byte)(lo & 0xFF);
            lo >>= 8;
        }
    }

    /// <summary>Value equality over both 64-bit words.</summary>
    public bool Equals(Ipv6Address other) => Hi == other.Hi && Lo == other.Lo;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Ipv6Address a && Equals(a);

    /// <inheritdoc/>
    public override int GetHashCode() => (int)(Hi ^ (Hi >> 32) ^ Lo ^ (Lo >> 32));

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Ipv6Address a, Ipv6Address b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Ipv6Address a, Ipv6Address b) => !a.Equals(b);

    /// <summary>
    /// Build the link-local address for a MAC using the modified EUI-64
    /// interface identifier (fe80::(mac^0200):..:ff:fe..).
    /// </summary>
    public static unsafe Ipv6Address LinkLocal(byte* mac)
    {
        Ipv6Address a = default;
        a.Hi = 0xFE80000000000000UL;
        ulong iid = ((ulong)(mac[0] ^ 0x02) << 56) | ((ulong)mac[1] << 48) |
                    ((ulong)mac[2] << 40) | (0xFFUL << 32) | (0xFEUL << 24) |
                    ((ulong)mac[3] << 16) | ((ulong)mac[4] << 8) | mac[5];
        a.Lo = iid;
        return a;
    }

    /// <summary>
    /// Format as RFC 5952 text: lowercase hex groups, longest zero run
    /// compressed with "::", no leading zeros.
    /// </summary>
    public override string ToString()
    {
        // Extract eight 16-bit groups.
        ushort[] g = new ushort[8];
        for (int i = 0; i < 4; i++)
            g[i] = (ushort)((Hi >> (48 - i * 16)) & 0xFFFFUL);
        for (int i = 0; i < 4; i++)
            g[4 + i] = (ushort)((Lo >> (48 - i * 16)) & 0xFFFFUL);

        // Longest run of zeros (>= 2 groups).
        int bestStart = -1, bestLen = 0;
        int curStart = -1, curLen = 0;
        for (int i = 0; i < 8; i++)
        {
            if (g[i] == 0)
            {
                if (curStart < 0)
                {
                    curStart = i;
                    curLen = 1;
                }
                else
                {
                    curLen++;
                }
                if (curLen > bestLen)
                {
                    bestLen = curLen;
                    bestStart = curStart;
                }
            }
            else
            {
                curStart = -1;
                curLen = 0;
            }
        }
        if (bestLen < 2)
            bestStart = -1;

        var sb = new System.Text.StringBuilder();
        bool first = true;
        for (int i = 0; i < 8;)
        {
            if (i == bestStart)
            {
                sb.Append("::");
                i += bestLen;
                first = true;   // "::" already carries the separator role
                continue;
            }
            if (!first)
                sb.Append(':');
            AppendHexGroup(sb, g[i]);
            first = false;
            i++;
        }
        if (sb.Length == 0)
            sb.Append("::");
        return sb.ToString();
    }

    /// <summary>
    /// Append a 16-bit group in lowercase hex without leading zeros.
    /// Manual digits: the Tier-0 JIT environment lacks
    /// UInt16.ToString(format, provider) (observed hard JIT failure).
    /// </summary>
    private static void AppendHexGroup(System.Text.StringBuilder sb, ushort v)
    {
        bool started = false;
        for (int shift = 12; shift >= 0; shift -= 4)
        {
            int d = (v >> shift) & 0xF;
            if (d != 0 || started || shift == 0)
            {
                sb.Append(d < 10 ? (char)('0' + d) : (char)('a' + d - 10));
                started = true;
            }
        }
    }

    /// <summary>
    /// Parse an IPv6 literal (full, compressed with "::", or v4-mapped
    /// "::ffff:a.b.c.d"). A trailing "%zone" suffix is ignored.
    /// Returns false when the text is not a valid IPv6 address.
    /// </summary>
    public static bool TryParse(string text, out Ipv6Address address)
    {
        address = default;
        if (string.IsNullOrEmpty(text))
            return false;

        // Strip zone id.
        int pct = text.IndexOf('%');
        if (pct >= 0)
            text = text.Substring(0, pct);

        // v4-mapped tail: "::ffff:1.2.3.4"
        int lastColon = text.LastIndexOf(':');
        if (lastColon >= 0 && text.IndexOf('.') > lastColon)
        {
            uint v4 = ParseV4Tail(text.Substring(lastColon + 1));
            if (v4 == 0xFFFFFFFF && text.Substring(lastColon + 1) != "255.255.255.255")
                return false;
            text = text.Substring(0, lastColon + 1) + "0:0";
            // Re-parse the combined form below; the embedded v4 becomes
            // the low 32 bits after the standard parse.
            if (!TryParseHexGroups(text, out address))
                return false;
            address.Lo = (address.Lo & 0xFFFFFFFF00000000UL) | v4;
            return true;
        }

        return TryParseHexGroups(text, out address);
    }

    private static uint ParseV4Tail(string s)
    {
        uint v = 0;
        int part = 0;
        uint acc = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '.')
            {
                if (part >= 4)
                    return 0xFFFFFFFF;
                v = (v << 8) | (acc & 0xFF);
                acc = 0;
                part++;
            }
            else if (c >= '0' && c <= '9')
            {
                acc = acc * 10 + (uint)(c - '0');
                if (acc > 255)
                    return 0xFFFFFFFF;
            }
            else
            {
                return 0xFFFFFFFF;
            }
        }
        if (part != 3)
            return 0xFFFFFFFF;
        v = (v << 8) | (acc & 0xFF);
        return v;
    }

    private static bool TryParseHexGroups(string text, out Ipv6Address address)
    {
        address = default;
        ushort[] g = new ushort[8];
        int filled = 0;

        // "::" split.
        int dc = text.IndexOf("::");
        string head = dc >= 0 ? text.Substring(0, dc) : text;
        string tail = dc >= 0 ? text.Substring(dc + 2) : null;

        int headCount = FillGroups(head, g, 0);
        if (headCount < 0)
            return false;
        filled = headCount;

        if (dc >= 0)
        {
            ushort[] t = new ushort[8];
            int tailCount = FillGroups(tail, t, 0);
            if (tailCount < 0)
                return false;
            if (headCount + tailCount > 8)
                return false;
            for (int i = 0; i < tailCount; i++)
                g[8 - tailCount + i] = t[i];
        }
        else if (filled != 8)
        {
            return false;
        }

        ulong hi = 0, lo = 0;
        for (int i = 0; i < 4; i++)
            hi = (hi << 16) | g[i];
        for (int i = 4; i < 8; i++)
            lo = (lo << 16) | g[i];
        address.Hi = hi;
        address.Lo = lo;
        return true;
    }

    private static int FillGroups(string s, ushort[] g, int at)
    {
        if (string.IsNullOrEmpty(s))
            return 0;
        int count = 0;
        int v = 0, digits = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || s[i] == ':')
            {
                if (digits == 0 || digits > 4 || at + count >= 8)
                    return -1;
                g[at + count] = (ushort)v;
                count++;
                v = 0;
                digits = 0;
            }
            else
            {
                char c = s[i];
                int d;
                if (c >= '0' && c <= '9')
                    d = c - '0';
                else if (c >= 'a' && c <= 'f')
                    d = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F')
                    d = c - 'A' + 10;
                else
                    return -1;
                v = (v << 4) | d;
                digits++;
            }
        }
        return count;
    }
}

/// <summary>
/// Parsed view of an IPv6 packet after walking extension headers.
/// </summary>
public unsafe struct Ipv6Packet
{
    /// <summary>Source address.</summary>
    public Ipv6Address Source;

    /// <summary>Destination address.</summary>
    public Ipv6Address Destination;

    /// <summary>Final next-header value (ICMPv6/TCP/UDP/...).</summary>
    public byte NextHeader;

    /// <summary>Hop limit field.</summary>
    public byte HopLimit;

    /// <summary>Pointer to the upper-layer payload.</summary>
    public byte* Payload;

    /// <summary>Upper-layer payload length.</summary>
    public int PayloadLength;

    /// <summary>Offset of the payload from the packet start.</summary>
    public int PayloadOffset;

    /// <summary>Fragment identification (0 when unfragmented).</summary>
    public uint FragmentId;

    /// <summary>Fragment offset in bytes (0 when first fragment/unfragmented).</summary>
    public int FragmentOffset;

    /// <summary>True when a fragment header was present.</summary>
    public bool HasFragmentHeader;

    /// <summary>Total IPv6 packet length (keyed by the header's payload length).</summary>
    public int TotalLength;
}

/// <summary>
/// IPv6 fixed header (40 bytes) helpers.
/// </summary>
public static unsafe class Ipv6
{
    /// <summary>Fixed header size.</summary>
    public const int HeaderSize = 40;

    /// <summary>Protocol number for the IPv6 layer.</summary>
    public const byte EthernetProtocol = 0x86;

    /// <summary>Build the 40-byte fixed header; returns HeaderSize.</summary>
    public static int BuildHeader(byte* buffer, Ipv6Address* src, Ipv6Address* dst,
        int payloadLength, byte nextHeader, byte hopLimit)
    {
        buffer[0] = 0x60;                       // version 6, traffic class 0
        buffer[1] = 0x00;
        buffer[2] = 0x00;
        buffer[3] = 0x00;                       // flow label
        buffer[4] = (byte)(payloadLength >> 8);
        buffer[5] = (byte)payloadLength;
        buffer[6] = nextHeader;
        buffer[7] = hopLimit;
        src->WriteTo(buffer + 8);
        dst->WriteTo(buffer + 24);
        return HeaderSize;
    }

    /// <summary>
    /// Parse an IPv6 packet, walking Hop-by-Hop, Routing, Fragment and
    /// Destination Options extension headers to reach the upper layer.
    /// </summary>
    public static bool Parse(byte* data, int length, out Ipv6Packet packet)
    {
        packet = default;
        if (data == null || length < HeaderSize)
            return false;
        if ((data[0] >> 4) != 6)
            return false;

        int payloadLen = (data[4] << 8) | data[5];
        int total = HeaderSize + payloadLen;
        if (total <= length)
            length = total;

        packet.Source = Ipv6Address.FromBytes(data + 8);
        packet.Destination = Ipv6Address.FromBytes(data + 24);
        packet.HopLimit = data[7];
        packet.TotalLength = total;

        byte next = data[6];
        int offset = HeaderSize;

        // Walk extension headers (bounded to avoid loops).
        for (int guard = 0; guard < 8; guard++)
        {
            if (next == Ipv6NextHeader.HopByHop || next == Ipv6NextHeader.Routing ||
                next == Ipv6NextHeader.DestinationOptions)
            {
                if (offset + 8 > length)
                    return false;
                byte nh = data[offset];
                int hlen = (data[offset + 1] + 1) * 8;
                if (hlen < 8 || offset + hlen > length)
                    return false;
                next = nh;
                offset += hlen;
            }
            else if (next == Ipv6NextHeader.Fragment)
            {
                if (offset + 8 > length)
                    return false;
                packet.HasFragmentHeader = true;
                packet.FragmentOffset = ((data[offset + 2] << 8) | data[offset + 3]) & 0xFFF8;
                packet.FragmentId = ((uint)data[offset + 4] << 24) | ((uint)data[offset + 5] << 16) |
                                    ((uint)data[offset + 6] << 8) | data[offset + 7];
                next = data[offset];
                offset += 8;
            }
            else
            {
                break;
            }
        }

        packet.NextHeader = next;
        packet.PayloadOffset = offset;
        packet.Payload = data + offset;
        packet.PayloadLength = length - offset;
        return packet.PayloadLength >= 0;
    }

    // ========================================================================
    // Checksums (IPv6 pseudo-header, RFC 8200 section 8.1)
    // ========================================================================

    /// <summary>
    /// Sum 16-bit big-endian words with an optional odd trailing byte.
    /// </summary>
    public static uint ChecksumSum(byte* data, int length, uint initial)
    {
        uint sum = initial;
        int i = 0;
        while (i + 1 < length)
        {
            sum += (uint)((data[i] << 8) | data[i + 1]);
            i += 2;
        }
        if (i < length)
            sum += (uint)(data[i] << 8);
        return sum;
    }

    /// <summary>Fold a 32-bit sum to its 16-bit complement.</summary>
    public static ushort FoldChecksum(uint sum)
    {
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>
    /// Add the IPv6 pseudo-header (src, dst, upper-layer length, zeros,
    /// next header) to a running sum. Addresses are passed by pointer:
    /// the Tier-0 JIT mishandles by-value 16-byte struct arguments.
    /// </summary>
    public static uint AddPseudoHeaderSum(uint sum, Ipv6Address* src, Ipv6Address* dst,
        uint upperLength, byte nextHeader)
    {
        byte* tmp = stackalloc byte[40];
        src->WriteTo(tmp);
        dst->WriteTo(tmp + 16);
        tmp[32] = (byte)(upperLength >> 24);
        tmp[33] = (byte)(upperLength >> 16);
        tmp[34] = (byte)(upperLength >> 8);
        tmp[35] = (byte)upperLength;
        tmp[36] = 0;
        tmp[37] = 0;
        tmp[38] = 0;
        tmp[39] = nextHeader;
        return ChecksumSum(tmp, 40, sum);
    }

    /// <summary>
    /// Compute an upper-layer checksum over pseudo-header + payload.
    /// The checksum field inside <paramref name="data"/> must be zero
    /// for the computation to be correct.
    /// </summary>
    public static ushort UpperLayerChecksum(Ipv6Address* src, Ipv6Address* dst,
        byte nextHeader, byte* data, int length)
    {
        uint sum = AddPseudoHeaderSum(0, src, dst, (uint)length, nextHeader);
        sum = ChecksumSum(data, length, sum);
        return FoldChecksum(sum);
    }

    /// <summary>
    /// Verify an upper-layer checksum that is already present in
    /// <paramref name="data"/> (field included in the sum; result 0
    /// when valid).
    /// </summary>
    public static bool VerifyUpperLayerChecksum(Ipv6Address* src, Ipv6Address* dst,
        byte nextHeader, byte* data, int length)
    {
        uint sum = AddPseudoHeaderSum(0, src, dst, (uint)length, nextHeader);
        sum = ChecksumSum(data, length, sum);
        return FoldChecksum(sum) == 0;
    }

    /// <summary>Map an IPv6 multicast address to its Ethernet MAC (33:33:xx:xx:xx:xx).</summary>
    public static void MulticastMac(Ipv6Address* addr, byte* mac)
    {
        mac[0] = 0x33;
        mac[1] = 0x33;
        uint lo = (uint)(addr->Lo & 0xFFFFFFFFUL);
        mac[2] = (byte)(lo >> 24);
        mac[3] = (byte)(lo >> 16);
        mac[4] = (byte)(lo >> 8);
        mac[5] = (byte)lo;
    }

    /// <summary>True when the address (by pointer) equals one of the locals.</summary>
    public static bool IsLocal(Ipv6Address* addr, Ipv6Address* localAddresses, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Ipv6Address* c = localAddresses + i;
            if (addr->Hi == c->Hi && addr->Lo == c->Lo)
                return true;
        }
        return false;
    }
}
