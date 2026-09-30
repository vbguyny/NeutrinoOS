// NeutrinoOS DDK - DHCPv6 client (RFC 8415 subset) - Phase 9 Task 2
//
// SOLICIT / ADVERTISE / REQUEST / REPLY exchange over UDPv6 with
// DUID-LL client identifiers. The client runs the exchange through the
// caller's frame pump (mirrors the v4 DhcpClient design) and reports
// the leased address, server and DNS servers.

using System;
using NeutrinoOS.DDK.Kernel;

namespace NeutrinoOS.DDK.Network.Stack;

/// <summary>DHCPv6 message types (RFC 8415 section 7.3).</summary>
public static class Dhcp6MessageType
{
    public const byte Solicit = 1;
    public const byte Advertise = 2;
    public const byte Request = 3;
    public const byte Confirm = 4;
    public const byte Renew = 5;
    public const byte Rebind = 6;
    public const byte Reply = 7;
    public const byte Release = 8;
    public const byte InfoRequest = 11;
}

    /// <summary>DHCPv6 option codes used by this client.</summary>
public static class Dhcp6OptionCode
{
    public const ushort ClientId = 1;
    public const ushort ServerId = 2;
    public const ushort IaNa = 3;
    public const ushort IaAddr = 5;
    public const ushort Oro = 6;
    public const ushort ElapsedTime = 8;
    public const ushort StatusCode = 13;
    public const ushort DnsServers = 23;
}

/// <summary>A DHCPv6 lease (what the REPLY carried).</summary>
public struct Dhcp6Lease
{
    public Ipv6Address Address;
    public uint PreferredLifetime;
    public uint ValidLifetime;
    public Ipv6Address Dns;
    public Ipv6Address ServerAddress;
}

/// <summary>DHCPv6 client (one interface, one IA_NA).</summary>
public unsafe class Dhcp6Client
{
    /// <summary>Delegate for transmitting a frame.</summary>
    public delegate void TransmitFrameDelegate(byte* data, int length);

    /// <summary>Delegate for receiving a frame.</summary>
    public delegate int ReceiveFrameDelegate(byte* buffer, int maxLength);

    private readonly NetworkStack _stack;
    private readonly uint _xid;
    private readonly uint _iaid;

    /// <summary>Create a client for the stack.</summary>
    public Dhcp6Client(NetworkStack stack)
    {
        _stack = stack;
        _xid = (uint)(Timer.GetUptimeMilliseconds() & 0xFFFFFF);
        _iaid = 0x01020304;
    }

    private static Ipv6Address AllDhcpServers()
    {
        Ipv6Address a = default;
        a.Hi = 0xFF02000000000000UL;
        a.Lo = 0x00010002;
        return a;
    }

    // ========================================================================
    // Message building
    // ========================================================================

    private static int PutOption(byte* buffer, int offset, ushort code, byte* data, int length)
    {
        buffer[offset] = (byte)(code >> 8);
        buffer[offset + 1] = (byte)code;
        buffer[offset + 2] = (byte)(length >> 8);
        buffer[offset + 3] = (byte)length;
        for (int i = 0; i < length; i++)
            buffer[offset + 4 + i] = data[i];
        return offset + 4 + length;
    }

    private int PutClientId(byte* buffer, int offset)
    {
        // DUID-LL: type 3, hwtype 1 (Ethernet), MAC.
        byte* duid = stackalloc byte[10];
        duid[0] = 0;
        duid[1] = 3;
        duid[2] = 0;
        duid[3] = 1;
        byte* mac = _stack.MacAddress;
        for (int i = 0; i < 6; i++)
            duid[4 + i] = mac[i];
        return PutOption(buffer, offset, Dhcp6OptionCode.ClientId, duid, 10);
    }

    private int PutIana(byte* buffer, int offset, bool withAddress, Ipv6Address* address)
    {
        byte* body = stackalloc byte[12 + (withAddress ? 24 : 0)];
        // IAID + T1 + T2
        body[0] = (byte)(_iaid >> 24);
        body[1] = (byte)(_iaid >> 16);
        body[2] = (byte)(_iaid >> 8);
        body[3] = (byte)_iaid;
        for (int i = 4; i < 12; i++)
            body[i] = 0;
        int len = 12;
        if (withAddress)
        {
            // Sub-option IAADDR: address, preferred, valid.
            body[12] = (byte)(Dhcp6OptionCode.IaAddr >> 8);
            body[13] = (byte)Dhcp6OptionCode.IaAddr;
            body[14] = 0;
            body[15] = 24;
            address->WriteTo(body + 16);
            for (int i = 32; i < 40; i++)
                body[i] = 0;
            len = 40;
        }
        return PutOption(buffer, offset, Dhcp6OptionCode.IaNa, body, len);
    }

    private static int PutOro(byte* buffer, int offset)
    {
        byte* oro = stackalloc byte[4];
        oro[0] = (byte)(Dhcp6OptionCode.DnsServers >> 8);
        oro[1] = (byte)Dhcp6OptionCode.DnsServers;
        oro[2] = 0;
        oro[3] = 25;   // domain search list
        return PutOption(buffer, offset, Dhcp6OptionCode.Oro, oro, 4);
    }

    private static int PutElapsed(byte* buffer, int offset)
    {
        byte* e = stackalloc byte[2];
        e[0] = 0;
        e[1] = 0;
        return PutOption(buffer, offset, Dhcp6OptionCode.ElapsedTime, e, 2);
    }

    private int PutClientLinkLayer(byte* buffer, int offset)
    {
        // RFC 8415 option 79: hwtype 1 (Ethernet) + link-layer address.
        byte* body = stackalloc byte[8];
        body[0] = 0;
        body[1] = 1;
        byte* mac = _stack.MacAddress;
        for (int i = 0; i < 6; i++)
            body[2 + i] = mac[i];
        return PutOption(buffer, offset, 79, body, 8);
    }

    private int BuildSolicit(byte* buffer)
    {
        buffer[0] = Dhcp6MessageType.Solicit;
        buffer[1] = (byte)(_xid >> 16);
        buffer[2] = (byte)(_xid >> 8);
        buffer[3] = (byte)_xid;
        int off = 4;
        off = PutElapsed(buffer, off);
        off = PutClientId(buffer, off);
        off = PutClientLinkLayer(buffer, off);
        off = PutIana(buffer, off, false, null);
        off = PutOro(buffer, off);
        return off;
    }

    /// <summary>
    /// Build an INFORMATION-REQUEST (RFC 3736 stateless DHCPv6).
    /// Per RFC 8415 the message carries no IA options; servers that only
    /// implement stateless service (e.g. QEMU's slirp) discard any
    /// message containing an IA option, so none is added here.
    /// </summary>
    private int BuildInfoRequest(byte* buffer)
    {
        buffer[0] = Dhcp6MessageType.InfoRequest;
        buffer[1] = (byte)(_xid >> 16);
        buffer[2] = (byte)(_xid >> 8);
        buffer[3] = (byte)_xid;
        int off = 4;
        off = PutElapsed(buffer, off);
        off = PutClientId(buffer, off);
        off = PutOro(buffer, off);
        return off;
    }

    private int BuildRequest(byte* buffer, Ipv6Address* serverId, Ipv6Address* offered)
    {
        buffer[0] = Dhcp6MessageType.Request;
        buffer[1] = (byte)(_xid >> 16);
        buffer[2] = (byte)(_xid >> 8);
        buffer[3] = (byte)_xid;
        int off = 4;
        off = PutElapsed(buffer, off);
        off = PutClientId(buffer, off);
        off = PutClientLinkLayer(buffer, off);
        // Server identifier: DUID of the server is echoed back (captured
        // from the ADVERTISE as raw bytes).
        off = PutServerIdEcho(buffer, off);
        off = PutIana(buffer, off, true, offered);
        off = PutOro(buffer, off);
        _ = serverId;
        return off;
    }

    private byte _serverDuidLen;
    private byte[] _serverDuid = new byte[64];

    private int PutServerIdEcho(byte* buffer, int offset)
    {
        byte* duid = stackalloc byte[64];
        for (int i = 0; i < _serverDuidLen; i++)
            duid[i] = _serverDuid[i];
        return PutOption(buffer, offset, Dhcp6OptionCode.ServerId, duid, _serverDuidLen);
    }

    // ========================================================================
    // Message parsing
    // ========================================================================

    /// <summary>Parse a server message; fills the lease/servers on REPLY/ADVERTISE.</summary>
    public bool ParseMessage(byte* data, int length, byte expectedType, out Dhcp6Lease lease)
    {
        lease = default;
        if (data == null || length < 4)
            return false;
        if (data[0] != expectedType)
            return false;

        int off = 4;
        while (off + 4 <= length)
        {
            ushort code = (ushort)((data[off] << 8) | data[off + 1]);
            ushort olen = (ushort)((data[off + 2] << 8) | data[off + 3]);
            off += 4;
            if (off + olen > length)
                return false;

            if (code == Dhcp6OptionCode.ServerId && olen <= 64)
            {
                _serverDuidLen = (byte)olen;
                for (int i = 0; i < olen; i++)
                    _serverDuid[i] = data[off + i];
            }
            else if (code == Dhcp6OptionCode.IaNa && olen >= 12)
            {
                // Walk sub-options for IAADDR.
                int sub = off + 12;
                int end = off + olen;
                while (sub + 4 <= end)
                {
                    ushort scode = (ushort)((data[sub] << 8) | data[sub + 1]);
                    ushort slen = (ushort)((data[sub + 2] << 8) | data[sub + 3]);
                    sub += 4;
                    if (sub + slen > end)
                        break;
                    if (scode == Dhcp6OptionCode.IaAddr && slen >= 24)
                    {
                        lease.Address = Ipv6Address.FromBytes(data + sub);
                        lease.PreferredLifetime =
                            ((uint)data[sub + 16] << 24) | ((uint)data[sub + 17] << 16) |
                            ((uint)data[sub + 18] << 8) | data[sub + 19];
                        lease.ValidLifetime =
                            ((uint)data[sub + 20] << 24) | ((uint)data[sub + 21] << 16) |
                            ((uint)data[sub + 22] << 8) | data[sub + 23];
                    }
                    sub += slen;
                }
            }
            else if (code == Dhcp6OptionCode.DnsServers && olen >= 16)
            {
                lease.Dns = Ipv6Address.FromBytes(data + off);
            }

            off += olen;
        }
        return true;
    }

    /// <summary>True when a DUID was captured from the last parsed message.</summary>
    public bool HasServerDuid => _serverDuidLen > 0;

    // ========================================================================
    // Exchange
    // ========================================================================

    /// <summary>
    /// Run the SOLICIT/REQUEST exchange. Returns the lease on success.
    /// The caller moves frames through the pump delegates while this
    /// method waits (the stack has no kernel pump).
    /// </summary>
    public bool Run(int timeoutMs, TransmitFrameDelegate transmit, ReceiveFrameDelegate receive,
        out Dhcp6Lease lease)
    {
        lease = default;
        if (!_stack.V6LinkLocalIsValid)
            _stack.ConfigureV6LinkLocal();

        Ipv6Address ll = _stack.V6LinkLocal;
        Ipv6Address allServers = AllDhcpServers();

        // ---- SOLICIT ----
        byte* tx = stackalloc byte[256];
        int len = BuildSolicit(tx);
        int frameLen = _stack.SendUdp6(&allServers, Dhcp6ClientPort(), 547, tx, len);
        if (frameLen <= 0)
        {
            Debug.WriteLine("[DHCP6] Failed to build SOLICIT frame");
            return false;
        }
        transmit(_stack.GetTxBuffer(), frameLen);

        ulong start = Timer.GetUptimeMilliseconds();
        byte* rx = stackalloc byte[1514];
        byte* payload = stackalloc byte[1500];
        Ipv6Address offer = default;
        Ipv6Address serverAddr = default;
        bool haveOffer = false;

        while (Timer.GetUptimeMilliseconds() - start < (ulong)timeoutMs)
        {
            int rxLen = receive(rx, 1514);
            if (rxLen > 0)
            {
                _stack.ProcessFrame(rx, rxLen);
                Ipv6Address srcAddr;
                ushort replyPort;
                int plen = _stack.ReceiveUdp6To(Dhcp6ClientPort(), out srcAddr, out replyPort,
                    payload, 1500);
                if (plen > 0)
                {
                    Dhcp6Lease l;
                    if (!haveOffer && ParseMessage(payload, plen, Dhcp6MessageType.Advertise, out l))
                    {
                        offer = l.Address;
                        serverAddr = srcAddr;
                        haveOffer = true;
                        Debug.WriteLine("[DHCP6] ADVERTISE received");
                        break;
                    }
                }
            }
            for (int spin = 0; spin < 4000; spin++)
            {
                // brief pacing so the wait yields to the emulated network
            }
        }

        if (!haveOffer)
        {
            Debug.WriteLine("[DHCP6] No ADVERTISE received");
            return false;
        }

        // ---- REQUEST ----
        len = BuildRequest(tx, &serverAddr, &offer);
        frameLen = _stack.SendUdp6(&serverAddr, Dhcp6ClientPort(), 547, tx, len);
        if (frameLen <= 0)
            return false;
        transmit(_stack.GetTxBuffer(), frameLen);

        start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < (ulong)timeoutMs)
        {
            int rxLen = receive(rx, 1514);
            if (rxLen > 0)
            {
                _stack.ProcessFrame(rx, rxLen);
                Ipv6Address srcAddr;
                ushort replyPort;
                int plen = _stack.ReceiveUdp6To(Dhcp6ClientPort(), out srcAddr, out replyPort,
                    payload, 1500);
                if (plen > 0)
                {
                    Dhcp6Lease l;
                    if (ParseMessage(payload, plen, Dhcp6MessageType.Reply, out l))
                    {
                        l.ServerAddress = srcAddr;
                        lease = l;
                        Debug.WriteLine("[DHCP6] REPLY received");
                        return true;
                    }
                }
            }
            for (int spin = 0; spin < 4000; spin++)
            {
                // brief pacing so the wait yields to the emulated network
            }
        }

        Debug.WriteLine("[DHCP6] No REPLY received");
        return false;
    }

    /// <summary>Client UDP port (546).</summary>
    public static ushort Dhcp6ClientPort() => 546;

    /// <summary>Server UDP port (547).</summary>
    public static ushort Dhcp6ServerPort() => 547;

    /// <summary>
    /// Run a stateless INFORMATION-REQUEST exchange and collect the
    /// configuration from the REPLY (DNS servers, server address).
    /// This is the mode stateless servers such as QEMU's slirp support;
    /// stateful SOLICIT/REQUEST exchanges need a full server.
    /// </summary>
    public bool RunInfoRequest(int timeoutMs, TransmitFrameDelegate transmit,
        ReceiveFrameDelegate receive, out Dhcp6Lease lease)
    {
        lease = default;
        if (!_stack.V6LinkLocalIsValid)
            _stack.ConfigureV6LinkLocal();

        Ipv6Address allServers = AllDhcpServers();
        byte* tx = stackalloc byte[256];
        int len = BuildInfoRequest(tx);
        int frameLen = _stack.SendUdp6(&allServers, Dhcp6ClientPort(), 547, tx, len);
        if (frameLen <= 0)
        {
            Debug.WriteLine("[DHCP6] Failed to build INFORMATION-REQUEST frame");
            return false;
        }
        transmit(_stack.GetTxBuffer(), frameLen);

        ulong start = Timer.GetUptimeMilliseconds();
        byte* rx = stackalloc byte[1514];
        byte* payload = stackalloc byte[1500];
        while (Timer.GetUptimeMilliseconds() - start < (ulong)timeoutMs)
        {
            int rxLen = receive(rx, 1514);
            if (rxLen > 0)
            {
                _stack.ProcessFrame(rx, rxLen);
                Ipv6Address srcAddr;
                ushort replyPort;
                int plen = _stack.ReceiveUdp6To(Dhcp6ClientPort(), out srcAddr, out replyPort,
                    payload, 1500);
                if (plen > 0)
                {
                    Dhcp6Lease l;
                    if (ParseMessage(payload, plen, Dhcp6MessageType.Reply, out l))
                    {
                        l.ServerAddress = srcAddr;
                        lease = l;
                        Debug.WriteLine("[DHCP6] stateless REPLY received");
                        return true;
                    }
                }
            }
            for (int spin = 0; spin < 4000; spin++)
            {
                // brief pacing so the wait yields to the emulated network
            }
        }

        Debug.WriteLine("[DHCP6] No stateless REPLY received");
        return false;
    }
}
