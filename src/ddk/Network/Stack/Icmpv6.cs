// NeutrinoOS DDK - ICMPv6 (RFC 4443) + Neighbor Discovery (RFC 4861)
// Echo for ping6, Neighbor Solicitation/Advertisement, Router
// Solicitation/Advertisement with Prefix Information, MTU and RDNSS
// options, and the checksum helpers needed by all of them.

using System;
using System.Runtime.InteropServices;

namespace NeutrinoOS.DDK.Network.Stack;

/// <summary>ICMPv6 message types used by this stack.</summary>
public static class Icmpv6Type
{
    public const byte DestinationUnreachable = 1;
    public const byte PacketTooBig = 2;
    public const byte TimeExceeded = 3;
    public const byte ParameterProblem = 4;
    public const byte EchoRequest = 128;
    public const byte EchoReply = 129;
    public const byte RouterSolicitation = 133;
    public const byte RouterAdvertisement = 134;
    public const byte NeighborSolicitation = 135;
    public const byte NeighborAdvertisement = 136;
    public const byte Redirect = 137;
}

/// <summary>NDP option type numbers (RFC 4861 section 4.6).</summary>
public static class NdpOptionType
{
    public const byte SourceLinkLayerAddress = 1;
    public const byte TargetLinkLayerAddress = 2;
    public const byte PrefixInformation = 3;
    public const byte RedirectedHeader = 4;
    public const byte Mtu = 5;
    public const byte RecursiveDnsServer = 25;   // RFC 8106
}

/// <summary>
/// Parsed ICMPv6 message header view.
/// </summary>
public unsafe struct Icmpv6Packet
{
    /// <summary>Message type.</summary>
    public byte Type;

    /// <summary>Message code.</summary>
    public byte Code;

    /// <summary>Pointer to the message body (after the 4-byte header).</summary>
    public byte* Body;

    /// <summary>Body length.</summary>
    public int BodyLength;

    /// <summary>Total message length.</summary>
    public int TotalLength;
}

/// <summary>
/// ICMPv6 build/parse helpers. Every message carries the IPv6
/// pseudo-header in its checksum, so builders need both addresses.
/// </summary>
public static unsafe class Icmpv6
{
    /// <summary>Protocol number for ICMPv6.</summary>
    public const byte ProtocolNumber = 58;

    /// <summary>Default hop limit for NDP messages (RFC 4861: 255).</summary>
    public const byte NdpHopLimit = 255;

    /// <summary>Verify the checksum of an ICMPv6 message.</summary>
    public static bool VerifyChecksum(byte* data, int length, Ipv6Address* src, Ipv6Address* dst)
    {
        return Ipv6.VerifyUpperLayerChecksum(src, dst, Ipv6NextHeader.Icmpv6, data, length);
    }

    private static void SetChecksum(byte* data, int length, Ipv6Address* src, Ipv6Address* dst)
    {
        data[2] = 0;
        data[3] = 0;
        ushort cksum = Ipv6.UpperLayerChecksum(src, dst, Ipv6NextHeader.Icmpv6, data, length);
        data[2] = (byte)(cksum >> 8);
        data[3] = (byte)cksum;
    }

    /// <summary>Parse an ICMPv6 message header.</summary>
    public static bool Parse(byte* data, int length, out Icmpv6Packet packet)
    {
        packet = default;
        if (data == null || length < 4)
            return false;
        packet.Type = data[0];
        packet.Code = data[1];
        packet.Body = data + 4;
        packet.BodyLength = length - 4;
        packet.TotalLength = length;
        return true;
    }

    // ========================================================================
    // Echo (ping6)
    // ========================================================================

    /// <summary>Build an ICMPv6 echo request/reply; returns total length.</summary>
    public static int BuildEcho(byte* buffer, bool request, ushort identifier, ushort sequence,
        byte* payload, int payloadLength, Ipv6Address* src, Ipv6Address* dst)
    {
        int total = 8 + payloadLength;
        buffer[0] = request ? Icmpv6Type.EchoRequest : Icmpv6Type.EchoReply;
        buffer[1] = 0;
        buffer[2] = 0;
        buffer[3] = 0;
        buffer[4] = (byte)(identifier >> 8);
        buffer[5] = (byte)identifier;
        buffer[6] = (byte)(sequence >> 8);
        buffer[7] = (byte)sequence;
        for (int i = 0; i < payloadLength; i++)
            buffer[8 + i] = payload[i];
        SetChecksum(buffer, total, src, dst);
        return total;
    }

    /// <summary>Extract identifier/sequence/payload from an echo message.</summary>
    public static bool ParseEcho(byte* data, int length, out ushort identifier,
        out ushort sequence, out byte* payload, out int payloadLength)
    {
        identifier = 0;
        sequence = 0;
        payload = null;
        payloadLength = 0;
        if (data == null || length < 8)
            return false;
        if (data[0] != Icmpv6Type.EchoRequest && data[0] != Icmpv6Type.EchoReply)
            return false;
        identifier = (ushort)((data[4] << 8) | data[5]);
        sequence = (ushort)((data[6] << 8) | data[7]);
        payload = data + 8;
        payloadLength = length - 8;
        return true;
    }

    // ========================================================================
    // Neighbor Discovery
    // ========================================================================

    /// <summary>Build a Router Solicitation (RFC 4861 4.1). Returns length (16).</summary>
    public static int BuildRouterSolicitation(byte* buffer, Ipv6Address* src,
        Ipv6Address* dst, byte* mac)
    {
        buffer[0] = Icmpv6Type.RouterSolicitation;
        buffer[1] = 0;
        buffer[2] = 0;
        buffer[3] = 0;
        for (int i = 4; i < 8; i++)
            buffer[i] = 0;
        // Source link-layer address option.
        buffer[8] = NdpOptionType.SourceLinkLayerAddress;
        buffer[9] = 1;                 // length in 8-byte units
        buffer[10] = mac[0];
        buffer[11] = mac[1];
        buffer[12] = mac[2];
        buffer[13] = mac[3];
        buffer[14] = mac[4];
        buffer[15] = mac[5];
        SetChecksum(buffer, 16, src, dst);
        return 16;
    }

    /// <summary>Build a Neighbor Solicitation (RFC 4861 4.3). Returns length (32).</summary>
    public static int BuildNeighborSolicitation(byte* buffer, Ipv6Address* src,
        Ipv6Address* target, byte* mac)
    {
        buffer[0] = Icmpv6Type.NeighborSolicitation;
        buffer[1] = 0;
        buffer[2] = 0;
        buffer[3] = 0;
        for (int i = 4; i < 8; i++)
            buffer[i] = 0;
        target->WriteTo(buffer + 8);
        // Source link-layer address option.
        buffer[24] = NdpOptionType.SourceLinkLayerAddress;
        buffer[25] = 1;
        buffer[26] = mac[0];
        buffer[27] = mac[1];
        buffer[28] = mac[2];
        buffer[29] = mac[3];
        buffer[30] = mac[4];
        buffer[31] = mac[5];
        Ipv6Address sol = target->SolicitedNode();
        SetChecksum(buffer, 32, src, &sol);
        return 32;
    }

    /// <summary>Build a Neighbor Advertisement (RFC 4861 4.4). Returns length (32).</summary>
    public static int BuildNeighborAdvertisement(byte* buffer, Ipv6Address* src, Ipv6Address* dst,
        Ipv6Address* target, bool router, bool solicited, bool overrideFlag, byte* mac, bool includeMac)
    {
        buffer[0] = Icmpv6Type.NeighborAdvertisement;
        buffer[1] = 0;
        buffer[2] = 0;
        buffer[3] = 0;
        buffer[4] = (byte)((router ? 0x80 : 0) | (solicited ? 0x40 : 0) | (overrideFlag ? 0x20 : 0));
        buffer[5] = 0;
        buffer[6] = 0;
        buffer[7] = 0;
        target->WriteTo(buffer + 8);
        int len = 24;
        if (includeMac && mac != null)
        {
            buffer[24] = NdpOptionType.TargetLinkLayerAddress;
            buffer[25] = 1;
            buffer[26] = mac[0];
            buffer[27] = mac[1];
            buffer[28] = mac[2];
            buffer[29] = mac[3];
            buffer[30] = mac[4];
            buffer[31] = mac[5];
            len = 32;
        }
        SetChecksum(buffer, len, src, dst);
        return len;
    }

    /// <summary>Read the target address of an NS/NA message.</summary>
    public static bool ParseNeighbor(byte* data, int length, out Ipv6Address target,
        out bool solicited, out bool hasMac, out byte* mac)
    {
        target = default;
        solicited = false;
        hasMac = false;
        mac = null;
        if (data == null || length < 24)
            return false;
        if (data[0] != Icmpv6Type.NeighborSolicitation && data[0] != Icmpv6Type.NeighborAdvertisement)
            return false;
        solicited = (data[4] & 0x40) != 0;
        target = Ipv6Address.FromBytes(data + 8);
        // Walk options for a link-layer address.
        int offset = 24;
        while (offset + 8 <= length)
        {
            byte otype = data[offset];
            int olen = data[offset + 1] * 8;
            if (olen == 0)
                break;
            if ((otype == NdpOptionType.SourceLinkLayerAddress ||
                 otype == NdpOptionType.TargetLinkLayerAddress) && olen >= 8)
            {
                hasMac = true;
                mac = data + offset + 2;
                break;
            }
            offset += olen;
        }
        return true;
    }

    /// <summary>
    /// Read a big-endian 64-bit value. Primitive-only helper: the Tier-0
    /// JIT miscompiles 16-byte struct field copies, so callers extract
    /// addresses as two u64 halves instead of materializing structs.
    /// </summary>
    public static ulong ReadU64(byte* p)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
            v = (v << 8) | p[i];
        return v;
    }
}
