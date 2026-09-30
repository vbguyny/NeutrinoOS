// NeutrinoOS DDK - NetworkStack IPv6 support (Phase 9 Task 2)
//
// Dual-stack extension of the network stack: IPv6 receive dispatch
// (ICMPv6, UDP, TCP), a neighbor-discovery cache, Router Advertisement
// processing with SLAAC address formation, IPv6 transmit with source
// selection, ping6 state and the loopback helpers.
//
// IMPORTANT (Tier-0 JIT): 16-byte IPv6 addresses cross JIT-compiled
// method boundaries BY POINTER, never by value - by-value 16-byte
// struct arguments miscompile (argument-layout mismatch producing
// wild writes). Struct RETURNS are fine (hidden return buffer).

using System;
using NeutrinoOS.DDK.Kernel;

namespace NeutrinoOS.DDK.Network.Stack;

public unsafe partial class NetworkStack
{
    // ========================================================================
    // IPv6 state
    // ========================================================================

    /// <summary>Link-local address (fe80::EUI-64 of the MAC) when configured.</summary>
    private Ipv6Address _v6LinkLocal;
    private bool _v6LinkLocalValid;

    /// <summary>Global address formed via SLAAC or set statically.</summary>
    private Ipv6Address _v6Global;
    private bool _v6GlobalValid;

    /// <summary>Default router learned from a Router Advertisement.</summary>
    private Ipv6Address _v6Gateway;
    private bool _v6RouterSeen;

    /// <summary>DNS server learned via RDNSS or DHCPv6.</summary>
    private Ipv6Address _v6Dns;

    /// <summary>Path MTU learned from Router Advertisement.</summary>
    private int _v6Mtu = 1500;

    // Neighbor discovery cache (parallel arrays; small fixed size).
    private const int MaxNdpEntries = 16;
    private Ipv6Address[] _ndpAddr;
    private byte[] _ndpMac;
    private bool[] _ndpValid;
    private int _ndpNextSlot;

    // IPv6 statistics.
    private ulong _icmp6Sent;
    private ulong _icmp6Received;
    private ulong _ndpSent;
    private ulong _ndpReceived;
    private ulong _udp6Sent;
    private ulong _udp6Received;
    private ulong _tcp6Sent;
    private ulong _tcp6Received;
    private ulong _ip6BytesIn;
    private ulong _ip6BytesOut;

    // ping6 pending-reply tracking.
    private bool _ping6Pending;
    private ushort _ping6Id;
    private ushort _ping6Seq;
    private Ipv6Address _ping6Target;
    private ulong _ping6Matched;

    private bool _v6InitDone;
    private bool _v6LoggedLinkLocal;

    /// <summary>Pointer-equality of two v6 addresses.</summary>
    private static bool AddrEq(Ipv6Address* a, Ipv6Address* b)
    {
        return a->Hi == b->Hi && a->Lo == b->Lo;
    }

    /// <summary>Initialize the IPv6 state. Called lazily on first use.</summary>
    private void EnsureV6()
    {
        if (_v6InitDone)
            return;
        _ndpAddr = new Ipv6Address[MaxNdpEntries];
        _ndpMac = new byte[MaxNdpEntries * 6];
        _ndpValid = new bool[MaxNdpEntries];
        _ndpNextSlot = 0;
        _v6InitDone = true;
    }

    // ========================================================================
    // Public configuration surface (used by ifconfig / ping6 / webhost)
    // ========================================================================

    /// <summary>True when any IPv6 address is configured.</summary>
    public bool V6Configured => _v6LinkLocalValid || _v6GlobalValid;

    /// <summary>True when the link-local address is configured.</summary>
    public bool V6LinkLocalIsValid => _v6LinkLocalValid;

    /// <summary>Link-local address (:: when not configured).</summary>
    public Ipv6Address V6LinkLocal => _v6LinkLocal;

    /// <summary>Global address followed by valid flag.</summary>
    public Ipv6Address V6Global => _v6Global;

    /// <summary>True when a global (non-link-local) address is configured.</summary>
    public bool V6GlobalValid => _v6GlobalValid;

    /// <summary>Default router (from RA / static; :: when unknown).</summary>
    public Ipv6Address V6Gateway => _v6Gateway;

    /// <summary>DNS server (RDNSS / DHCPv6; :: when unknown).</summary>
    public Ipv6Address V6Dns => _v6Dns;

    /// <summary>True when a router advertisement has been received.</summary>
    public bool V6RouterSeen => _v6RouterSeen;

    /// <summary>Current IPv6 MTU.</summary>
    public int V6Mtu => _v6Mtu;

    /// <summary>ICMPv6 counters: echo requests sent / replies received.</summary>
    public ulong Icmp6Sent => _icmp6Sent;
    public ulong Icmp6Received => _icmp6Received;
    public ulong NdpSent => _ndpSent;
    public ulong NdpReceived => _ndpReceived;
    public ulong Udp6Sent => _udp6Sent;
    public ulong Udp6Received => _udp6Received;
    public ulong Tcp6Sent => _tcp6Sent;
    public ulong Tcp6Received => _tcp6Received;
    public ulong Ip6BytesIn => _ip6BytesIn;
    public ulong Ip6BytesOut => _ip6BytesOut;

    /// <summary>
    /// Configure the link-local address from the interface MAC. The
    /// caller (ifconfig / boot path) decides when to opt in.
    /// </summary>
    public void ConfigureV6LinkLocal()
    {
        EnsureV6();
        _v6LinkLocal = Ipv6Address.LinkLocal(_macAddress);
        _v6LinkLocalValid = true;
        Ipv6Address ll = _v6LinkLocal;
        NdpLearn(&ll, _macAddress);   // ourselves (for lookups)
        if (!_v6LoggedLinkLocal)
        {
            _v6LoggedLinkLocal = true;
            Debug.Write("[NetStack] IPv6 link-local: ");
            Debug.WriteLine(ll.ToString());
        }
    }

    /// <summary>Set a static global address (+ optional gateway/DNS).</summary>
    public void ConfigureV6Static(Ipv6Address* global, Ipv6Address* gateway, Ipv6Address* dns)
    {
        EnsureV6();
        if (!_v6LinkLocalValid)
            ConfigureV6LinkLocal();
        _v6Global = *global;
        _v6GlobalValid = true;
        _v6Gateway = *gateway;
        _v6RouterSeen = !(gateway->Hi == 0 && gateway->Lo == 0);
        if (!(dns->Hi == 0 && dns->Lo == 0))
            _v6Dns = *dns;
        Debug.Write("[NetStack] IPv6 static address: ");
        Debug.WriteLine(_v6Global.ToString());
    }

    /// <summary>
    /// Set only the DNSv6 server (used for stateless DHCPv6 / RDNSS
    /// follow-ups). Primitive writes only - see the RA parser note.
    /// </summary>
    public void SetV6Dns(Ipv6Address* dns)
    {
        if (dns->Hi == 0 && dns->Lo == 0)
            return;
        _v6Dns.Hi = dns->Hi;
        _v6Dns.Lo = dns->Lo;
        Debug.Write("[NetStack] IPv6 DNS: ");
        Debug.WriteLine(_v6Dns.ToString());
    }

    /// <summary>Forget the DNSv6 server (dual-stack DNS fallback).</summary>
    public void ClearV6Dns()
    {
        _v6Dns.Hi = 0;
        _v6Dns.Lo = 0;
    }

    // ========================================================================
    // Neighbor discovery cache
    // ========================================================================

    /// <summary>Learn/refresh a neighbor's MAC.</summary>
    private void NdpLearn(Ipv6Address* addr, byte* mac)
    {
        if (mac == null || _ndpValid == null)
            return;
        for (int i = 0; i < MaxNdpEntries; i++)
        {
            if (_ndpValid[i] && _ndpAddr[i].Hi == addr->Hi && _ndpAddr[i].Lo == addr->Lo)
            {
                for (int j = 0; j < 6; j++)
                    _ndpMac[i * 6 + j] = mac[j];
                return;
            }
        }
        int slot = _ndpNextSlot;
        _ndpNextSlot = (_ndpNextSlot + 1) % MaxNdpEntries;
        _ndpAddr[slot] = *addr;
        _ndpValid[slot] = true;
        for (int j = 0; j < 6; j++)
            _ndpMac[slot * 6 + j] = mac[j];
    }

    /// <summary>Look up a neighbor MAC; returns false when unknown.</summary>
    public bool LookupNdp(Ipv6Address* addr, byte* macOut)
    {
        if (_ndpValid == null)
            return false;
        for (int i = 0; i < MaxNdpEntries; i++)
        {
            if (_ndpValid[i] && _ndpAddr[i].Hi == addr->Hi && _ndpAddr[i].Lo == addr->Lo)
            {
                for (int j = 0; j < 6; j++)
                    macOut[j] = _ndpMac[i * 6 + j];
                return true;
            }
        }
        return false;
    }

    /// <summary>Number of cached neighbors (tests).</summary>
    public int NdpEntryCount()
    {
        if (_ndpValid == null)
            return 0;
        int n = 0;
        for (int i = 0; i < MaxNdpEntries; i++)
            if (_ndpValid[i])
                n++;
        return n;
    }

    // ========================================================================
    // Receive path
    // ========================================================================

    /// <summary>
    /// True when a multicast destination address is one this node listens to.
    /// </summary>
    private bool IsJoinedMulticast(Ipv6Address* dst)
    {
        if (dst->Hi == 0xFF02000000000000UL)
        {
            // ff02::1 (all nodes), ff02::2 (all routers), ff02::1:2 (DHCPv6)
            if (dst->Lo == 1 || dst->Lo == 2 || dst->Lo == 0x00010002)
                return true;
        }
        if (_v6LinkLocalValid)
        {
            Ipv6Address n = _v6LinkLocal.SolicitedNode();
            if (AddrEq(dst, &n))
                return true;
        }
        if (_v6GlobalValid)
        {
            Ipv6Address n = _v6Global.SolicitedNode();
            if (AddrEq(dst, &n))
                return true;
        }
        return false;
    }

    /// <summary>True when the address is assigned to this interface (or ::1).</summary>
    public bool IsLocalV6(Ipv6Address* addr)
    {
        if (addr->Hi == 0 && addr->Lo == 1)
            return true;
        if (_v6LinkLocalValid && _v6LinkLocal.Hi == addr->Hi && _v6LinkLocal.Lo == addr->Lo)
            return true;
        if (_v6GlobalValid && _v6Global.Hi == addr->Hi && _v6Global.Lo == addr->Lo)
            return true;
        return false;
    }

    /// <summary>Process an IPv6 packet (called from the EtherType dispatch).</summary>
    private void ProcessIPv6(byte* data, int length, byte* ethSrc)
    {
        Ipv6Packet pkt;
        if (!Ipv6.Parse(data, length, out pkt))
            return;

        _ip6BytesIn += (ulong)pkt.TotalLength;
        if (pkt.NextHeader == Ipv6NextHeader.Tcp)
            _tcp6Received++;

        // Fragment handling: process the first fragment only; later
        // fragments are dropped (no reassembly - documented limitation).
        if (pkt.HasFragmentHeader && pkt.FragmentOffset != 0)
            return;

        Ipv6Address* psrc = &pkt.Source;
        Ipv6Address* pdst = &pkt.Destination;

        bool forUs = IsLocalV6(pdst);
        if (!forUs && pdst->IsMulticast)
            forUs = IsJoinedMulticast(pdst);
        if (!forUs)
            return;

        // Any inbound packet with a usable source teaches us the
        // sender's MAC (snooping, like the ARP cache).
        if (ethSrc != null && !(psrc->Hi == 0 && psrc->Lo == 0) && !psrc->IsMulticast)
            NdpLearn(psrc, ethSrc);

        switch (pkt.NextHeader)
        {
            case Ipv6NextHeader.Icmpv6:
                ProcessIcmpv6(pkt.Payload, pkt.PayloadLength, psrc, pdst);
                break;
            case Ipv6NextHeader.Udp:
                ProcessUdp6(pkt.Payload, pkt.PayloadLength, psrc, pdst);
                break;
            case Ipv6NextHeader.Tcp:
                ProcessTcp6(pkt.Payload, pkt.PayloadLength, psrc, pdst);
                break;
            default:
                Debug.Write("[NetStack] IPv6 proto=");
                Debug.WriteDecimal(pkt.NextHeader);
                Debug.Write(" from ");
                Debug.WriteLine(pkt.Source.ToString());
                break;
        }
    }

    /// <summary>Process an ICMPv6 message.</summary>
    private void ProcessIcmpv6(byte* data, int length, Ipv6Address* src, Ipv6Address* dst)
    {
        _icmp6Received++;

        if (!Icmpv6.VerifyChecksum(data, length, src, dst))
        {
            Debug.WriteLine("[NetStack] ICMPv6 checksum invalid");
            return;
        }

        switch (data[0])
        {
            case Icmpv6Type.EchoRequest:
            {
                ushort id;
                ushort seq;
                byte* payload;
                int payloadLen;
                if (!Icmpv6.ParseEcho(data, length, out id, out seq, out payload, out payloadLen))
                    return;
                // Never answer a request addressed to a multicast group.
                if (dst->IsMulticast)
                    return;
                SendIcmpv6Echo(false, src, id, seq, payload, payloadLen);
                break;
            }

            case Icmpv6Type.EchoReply:
            {
                ushort id;
                ushort seq;
                byte* payload;
                int payloadLen;
                if (!Icmpv6.ParseEcho(data, length, out id, out seq, out payload, out payloadLen))
                    return;
                if (_ping6Pending && _ping6Id == id && _ping6Seq == seq &&
                    _ping6Target.Hi == src->Hi && _ping6Target.Lo == src->Lo)
                {
                    _ping6Pending = false;
                    _ping6Matched++;
                }
                break;
            }

            case Icmpv6Type.RouterAdvertisement:
            {
                _ndpReceived++;
                AdoptRouterAdvertisement(data, length, src);
                break;
            }

            case Icmpv6Type.RouterSolicitation:
                _ndpReceived++;
                // A host does not answer router solicitations.
                break;

            case Icmpv6Type.NeighborSolicitation:
            {
                _ndpReceived++;
                Ipv6Address target;
                bool solicited;
                bool hasMac;
                byte* mac;
                Icmpv6.ParseNeighbor(data, length, out target, out solicited, out hasMac, out mac);
                if (!IsLocalV6(&target))
                    return;
                // Reply to the requester, or to all-nodes for DAD
                // (unspecified source - duplicate address detection).
                bool dad = src->Hi == 0 && src->Lo == 0;
                Ipv6Address replyDst = default;
                if (dad)
                {
                    replyDst.Hi = 0xFF02000000000000UL;
                    replyDst.Lo = 1;
                }
                else
                {
                    replyDst = *src;
                }
                SendNeighborAdvertisement(&target, &replyDst, dad);
                break;
            }

            case Icmpv6Type.NeighborAdvertisement:
            {
                _ndpReceived++;
                Ipv6Address target;
                bool solicited;
                bool hasMac;
                byte* mac;
                Icmpv6.ParseNeighbor(data, length, out target, out solicited, out hasMac, out mac);
                if (hasMac)
                    NdpLearn(&target, mac);
                break;
            }

            case Icmpv6Type.Redirect:
                _ndpReceived++;
                break;

            default:
                break;
        }
    }

    /// <summary>Adopt the usable parts of a Router Advertisement (SLAAC).</summary>
    private void AdoptRouterAdvertisement(byte* data, int length, Ipv6Address* router)
    {
        if (data == null || length < 16 || data[0] != Icmpv6Type.RouterAdvertisement)
            return;

        _v6RouterSeen = true;
        if (!_v6LinkLocalValid)
            ConfigureV6LinkLocal();

        ushort lifetime = (ushort)((data[6] << 8) | data[7]);

        // Option walk. Every value stays in primitives: the Tier-0 JIT
        // miscompiles 16-byte struct copies out of a parsed local (and
        // out-parameters of large structs), so addresses from the wire
        // are read as two u64 halves and written straight into the stack
        // fields. Never materialize an Ipv6Address local here.
        byte* mac = stackalloc byte[6];
        bool haveMac = false;
        bool slaacDone = false;

        int offset = 16;
        while (offset + 2 <= length)
        {
            byte otype = data[offset];
            int olen = data[offset + 1] * 8;
            if (olen == 0 || offset + olen > length)
                break;

            if (otype == NdpOptionType.SourceLinkLayerAddress && olen >= 8)
            {
                if (data[offset + 2] != 0)
                {
                    for (int i = 0; i < 6; i++)
                        mac[i] = data[offset + 2 + i];
                    haveMac = true;
                }
            }
            else if (otype == NdpOptionType.PrefixInformation && olen >= 32 && !slaacDone)
            {
                slaacDone = true;
                byte plen = data[offset + 2];
                byte flags = data[offset + 3];
                bool autonomous = (flags & 0x40) != 0;
                if (autonomous && plen == 64)
                {
                    ulong prefixHi = Icmpv6.ReadU64(data + offset + 16);
                    if (prefixHi != 0)
                    {
                        _v6Global.Hi = prefixHi;
                        _v6Global.Lo = _v6LinkLocal.Lo;
                        _v6GlobalValid = true;
                        Debug.Write("[NetStack] IPv6 SLAAC address: ");
                        Debug.WriteLine(_v6Global.ToString());
                    }
                }
            }
            else if (otype == NdpOptionType.RecursiveDnsServer && olen >= 24)
            {
                ulong dnsHi = Icmpv6.ReadU64(data + offset + 8);
                ulong dnsLo = Icmpv6.ReadU64(data + offset + 16);
                if (!(dnsHi == 0 && dnsLo == 0))
                {
                    _v6Dns.Hi = dnsHi;
                    _v6Dns.Lo = dnsLo;
                }
            }
            else if (otype == NdpOptionType.Mtu && olen >= 8)
            {
                uint mtu = ((uint)data[offset + 4] << 24) | ((uint)data[offset + 5] << 16) |
                           ((uint)data[offset + 6] << 8) | data[offset + 7];
                if (mtu >= 1280 && mtu <= 1500)
                    _v6Mtu = (int)mtu;
            }

            offset += olen;
        }

        if (haveMac)
            NdpLearn(router, mac);

        if (lifetime > 0)
        {
            _v6Gateway = *router;
            Debug.Write("[NetStack] IPv6 router: ");
            Debug.WriteLine(_v6Gateway.ToString());
        }
    }

    // ========================================================================
    // ping6 support
    // ========================================================================

    /// <summary>Arm the ping6 matcher for the next echo.</summary>
    public void ArmPing6(ushort id, ushort seq, Ipv6Address* target)
    {
        _ping6Id = id;
        _ping6Seq = seq;
        _ping6Target = *target;
        _ping6Pending = true;
    }

    /// <summary>True (and consumed) when a matching echo reply arrived.</summary>
    public bool TakePing6Match()
    {
        if (_ping6Matched == 0)
            return false;
        _ping6Matched = 0;
        return true;
    }

    // ========================================================================
    // Transmit path
    // ========================================================================

    /// <summary>Pick the source address for a destination (link-local fallback).</summary>
    public Ipv6Address SelectSourceV6(Ipv6Address* dst)
    {
        // All multicast this stack sends is link-scope (ff02::/16): RFC
        // 4291 requires a link-local source, and DHCPv6 (RFC 8415) does
        // too. It also keeps replies resolvable without an NDP dance.
        if (_v6LinkLocalValid && (dst->IsLinkLocal || dst->IsMulticast))
            return _v6LinkLocal;
        if (_v6GlobalValid)
            return _v6Global;
        return _v6LinkLocal;
    }

    /// <summary>
    /// Build an IPv6 frame in the TX buffer. Returns the frame length, or
    /// 0 when the next-hop MAC is unknown (NDP resolution required first).
    /// NDP messages (RS/NS/NA) must use hop limit 255 per RFC 4861 and are
    /// dropped by receivers (including QEMU slirp) otherwise.
    /// </summary>
    public int BuildIpv6Frame(Ipv6Address* src, Ipv6Address* dst, byte nextHeader,
        byte* payload, int payloadLen, byte hopLimit = 64)
    {
        if (!_v6LinkLocalValid)
            return 0;

        int ipLen = Ipv6.HeaderSize + payloadLen;
        int totalLen = 14 + ipLen;
        if (totalLen > 1514)
            return 0;

        // Resolve next-hop MAC.
        byte* destMac = stackalloc byte[6];
        if (dst->IsMulticast)
        {
            Ipv6.MulticastMac(dst, destMac);
        }
        else if ((dst->Hi == 0 && dst->Lo == 1) || IsLocalV6(dst))
        {
            for (int i = 0; i < 6; i++)
                destMac[i] = _macAddress[i];
        }
        else
        {
            // On-link when the destination shares our /64 prefix;
            // otherwise the next hop is the default router.
            Ipv6Address nextHop = *dst;
            if (_v6GlobalValid &&
                (dst->Hi & 0xFFFFFFFF00000000UL) != (_v6Global.Hi & 0xFFFFFFFF00000000UL))
            {
                nextHop = _v6Gateway;
            }
            if (!LookupNdp(&nextHop, destMac))
                return 0;
        }

        Ethernet.BuildHeader(_txBuffer, destMac, _macAddress, EtherType.IPv6);
        Ipv6.BuildHeader(_txBuffer + 14, src, dst, payloadLen, nextHeader, hopLimit);
        for (int i = 0; i < payloadLen; i++)
            _txBuffer[14 + Ipv6.HeaderSize + i] = payload[i];

        if (totalLen < EthernetHeader.MinFrameSize)
        {
            for (int i = totalLen; i < EthernetHeader.MinFrameSize; i++)
                _txBuffer[i] = 0;
            totalLen = EthernetHeader.MinFrameSize;
        }

        _ip6BytesOut += (ulong)ipLen;
        return totalLen;
    }

    /// <summary>Build + queue an ICMPv6 echo; returns the frame length.</summary>
    public int SendIcmpv6Echo(bool request, Ipv6Address* dst, ushort id, ushort seq,
        byte* payload, int payloadLen)
    {
        if (!_v6LinkLocalValid)
            ConfigureV6LinkLocal();
        // Source selection without chained struct returns (JIT hazard).
        Ipv6Address src = _v6LinkLocal;
        if (dst->Hi == 0 && dst->Lo == 1)
        {
            src.Hi = 0;
            src.Lo = 1;
        }
        else if (!dst->IsLinkLocal && _v6GlobalValid)
        {
            src = _v6Global;
        }

        byte* icmp = stackalloc byte[8 + payloadLen];
        int icmpLen = Icmpv6.BuildEcho(icmp, request, id, seq, payload, payloadLen, &src, dst);
        int frameLen = BuildIpv6Frame(&src, dst, Ipv6NextHeader.Icmpv6, icmp, icmpLen);
        if (frameLen > 0)
        {
            _pendingTxLen = frameLen;
            _icmp6Sent++;
        }
        return frameLen;
    }

    /// <summary>Send a Router Solicitation (ff02::2); returns frame length.</summary>
    public int SendRouterSolicitation()
    {
        if (!_v6LinkLocalValid)
            ConfigureV6LinkLocal();
        Ipv6Address dst = default;
        dst.Hi = 0xFF02000000000000UL;
        dst.Lo = 2;
        Ipv6Address ll = _v6LinkLocal;
        byte* icmp = stackalloc byte[16];
        int len = Icmpv6.BuildRouterSolicitation(icmp, &ll, &dst, _macAddress);
        int frameLen = BuildIpv6Frame(&ll, &dst, Ipv6NextHeader.Icmpv6, icmp, len,
            Icmpv6.NdpHopLimit);
        if (frameLen > 0)
        {
            _pendingTxLen = frameLen;
            _ndpSent++;
        }
        return frameLen;
    }

    /// <summary>Send a Neighbor Solicitation for a target; returns frame length.</summary>
    public int SendNeighborSolicitation(Ipv6Address* target)
    {
        if (!_v6LinkLocalValid)
            ConfigureV6LinkLocal();
        // Source for a resolution probe is always the link-local address.
        Ipv6Address dst = target->SolicitedNode();
        Ipv6Address ll = _v6LinkLocal;
        byte* icmp = stackalloc byte[32];
        int len = Icmpv6.BuildNeighborSolicitation(icmp, &ll, target, _macAddress);
        int frameLen = BuildIpv6Frame(&ll, &dst, Ipv6NextHeader.Icmpv6, icmp, len,
            Icmpv6.NdpHopLimit);
        if (frameLen > 0)
        {
            _pendingTxLen = frameLen;
            _ndpSent++;
        }
        return frameLen;
    }

    /// <summary>Answer a Neighbor Solicitation with an advertisement.</summary>
    private void SendNeighborAdvertisement(Ipv6Address* target, Ipv6Address* dst, bool solicited)
    {
        byte* icmp = stackalloc byte[32];
        int len = Icmpv6.BuildNeighborAdvertisement(icmp, target, dst, target,
            false, solicited, true, _macAddress, true);
        int frameLen = BuildIpv6Frame(target, dst, Ipv6NextHeader.Icmpv6, icmp, len,
            Icmpv6.NdpHopLimit);
        if (frameLen > 0)
        {
            _pendingTxLen = frameLen;
            _ndpSent++;
        }
    }

    // ========================================================================
    // UDP over IPv6
    // ========================================================================

    /// <summary>
    /// Send a UDP datagram over IPv6. Returns the frame length, or 0 when
    /// the next-hop MAC is unknown.
    /// </summary>
    public int SendUdp6(Ipv6Address* dst, ushort srcPort, ushort dstPort, byte* data, int dataLen)
    {
        if (dataLen > 1400)
            return 0;
        if (!_v6LinkLocalValid)
            ConfigureV6LinkLocal();

        Ipv6Address src = SelectSourceV6(dst);
        byte* udp = stackalloc byte[8 + dataLen];
        udp[0] = (byte)(srcPort >> 8);
        udp[1] = (byte)srcPort;
        udp[2] = (byte)(dstPort >> 8);
        udp[3] = (byte)dstPort;
        int ulen = 8 + dataLen;
        udp[4] = (byte)(ulen >> 8);
        udp[5] = (byte)ulen;
        udp[6] = 0;
        udp[7] = 0;
        for (int i = 0; i < dataLen; i++)
            udp[8 + i] = data[i];
        ushort cksum = Ipv6.UpperLayerChecksum(&src, dst, Ipv6NextHeader.Udp, udp, ulen);
        if (cksum == 0)
            cksum = 0xFFFF;   // 0 is illegal for UDPv6 (means "no checksum")
        udp[6] = (byte)(cksum >> 8);
        udp[7] = (byte)cksum;

        int frameLen = BuildIpv6Frame(&src, dst, Ipv6NextHeader.Udp, udp, ulen);
        if (frameLen > 0)
        {
            _pendingTxLen = frameLen;
            _udp6Sent++;
        }
        return frameLen;
    }

    /// <summary>Process a received UDPv6 datagram.</summary>
    private void ProcessUdp6(byte* data, int length, Ipv6Address* src, Ipv6Address* dst)
    {
        _udp6Received++;
        if (length < 8)
            return;
        ushort srcPort = (ushort)((data[0] << 8) | data[1]);
        ushort dstPort = (ushort)((data[2] << 8) | data[3]);
        int ulen = (data[4] << 8) | data[5];
        if (ulen >= 8 && ulen <= length)
            length = ulen;

        // Checksum is mandatory for IPv6 UDP.
        if (!Ipv6.VerifyUpperLayerChecksum(src, dst, Ipv6NextHeader.Udp, data, length))
        {
            if (!Quiet)
                Debug.WriteLine("[NetStack] UDPv6 checksum invalid");
            return;
        }

        int payloadLen = length - 8;
        if (_udpQueueCount < MaxUdpQueueSize && payloadLen <= MaxUdpDatagramSize)
        {
            int idx = _udpQueueTail;
            _udpQueue[idx].SourceIP = 0;
            _udpQueue[idx].SourceV6 = *src;
            _udpQueue[idx].DestV6 = *dst;
            _udpQueue[idx].IsV6 = true;
            _udpQueue[idx].SourcePort = srcPort;
            _udpQueue[idx].DestPort = dstPort;
            _udpQueue[idx].Length = payloadLen;
            _udpQueue[idx].Valid = true;
            fixed (byte* dest = _udpQueue[idx].Data)
            {
                for (int i = 0; i < payloadLen; i++)
                    dest[i] = data[8 + i];
            }
            _udpQueueTail = (_udpQueueTail + 1) % MaxUdpQueueSize;
            _udpQueueCount++;
        }
        else
        {
            Debug.WriteLine("[NetStack] UDPv6 queue full, dropping packet");
        }
    }

    /// <summary>
    /// Receive a UDPv6 datagram matching source/destination ports.
    /// Non-matching entries are consumed (mirrors the v4 helper).
    /// </summary>
    public int ReceiveUdp6Matching(ushort wantSrcPort, ushort wantDestPort,
        out Ipv6Address srcAddr, byte* buffer, int bufferLen)
    {
        srcAddr = default;
        int wantSrc = wantSrcPort;
        int wantDest = wantDestPort;
        for (int guard = 0; guard < MaxUdpQueueSize; guard++)
        {
            if (_udpQueueCount == 0)
                return 0;
            int idx = _udpQueueHead;
            if (!_udpQueue[idx].Valid)
                return 0;
            bool isV6 = _udpQueue[idx].IsV6;
            int src = _udpQueue[idx].SourcePort;
            int dest = _udpQueue[idx].DestPort;
            int len = _udpQueue[idx].Length;
            Ipv6Address srcA = _udpQueue[idx].SourceV6;

            bool match = isV6 && src == wantSrc && dest == wantDest;

            _udpQueue[idx].Valid = false;
            _udpQueueHead = (_udpQueueHead + 1) % MaxUdpQueueSize;
            _udpQueueCount--;

            if (match && len > 0)
            {
                int copyLen = len < bufferLen ? len : bufferLen;
                fixed (byte* srcP = _udpQueue[idx].Data)
                {
                    for (int i = 0; i < copyLen; i++)
                        buffer[i] = srcP[i];
                }
                srcAddr = srcA;
                return copyLen;
            }
        }
        return 0;
    }

    /// <summary>Count of queued UDPv6 datagrams (tests).</summary>
    public int Udp6Available()
    {
        int n = 0;
        for (int i = 0; i < _udpQueueCount; i++)
        {
            int idx = (_udpQueueHead + i) % MaxUdpQueueSize;
            if (_udpQueue[idx].Valid && _udpQueue[idx].IsV6)
                n++;
        }
        return n;
    }

    /// <summary>
    /// Receive a UDPv6 datagram addressed to a destination port,
    /// regardless of the source port. Servers such as QEMU slirp reply
    /// to DHCPv6 from an ephemeral port (the pcap shows 8962, not 547),
    /// so src-port matching cannot be used for those flows. Non-matching
    /// entries are consumed (queue hygiene, mirrors the other helpers).
    /// </summary>
    public int ReceiveUdp6To(ushort destPort, out Ipv6Address srcAddr, out ushort srcPort,
        byte* buffer, int bufferLen)
    {
        srcAddr = default;
        srcPort = 0;
        int wantDest = destPort;
        for (int guard = 0; guard < MaxUdpQueueSize; guard++)
        {
            if (_udpQueueCount == 0)
                return 0;
            int idx = _udpQueueHead;
            if (!_udpQueue[idx].Valid)
                return 0;
            bool isV6 = _udpQueue[idx].IsV6;
            int src = _udpQueue[idx].SourcePort;
            int dest = _udpQueue[idx].DestPort;
            int len = _udpQueue[idx].Length;
            Ipv6Address srcA = _udpQueue[idx].SourceV6;

            bool match = isV6 && dest == wantDest;

            _udpQueue[idx].Valid = false;
            _udpQueueHead = (_udpQueueHead + 1) % MaxUdpQueueSize;
            _udpQueueCount--;

            if (match && len > 0)
            {
                int copyLen = len < bufferLen ? len : bufferLen;
                fixed (byte* srcP = _udpQueue[idx].Data)
                {
                    for (int i = 0; i < copyLen; i++)
                        buffer[i] = srcP[i];
                }
                srcAddr = srcA;
                srcPort = (ushort)src;
                return copyLen;
            }
        }
        return 0;
    }

    // ========================================================================
    // TCP over IPv6 - addressed in NetworkStack.Ipv6.Tcp.cs
    // ========================================================================
}
