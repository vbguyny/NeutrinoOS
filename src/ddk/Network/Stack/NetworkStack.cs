// ProtonOS DDK - Network Stack Manager
// Ties together Ethernet, ARP, and higher protocol layers

using System;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network.Sockets;

namespace ProtonOS.DDK.Network.Stack;

/// <summary>
/// Network interface configuration.
/// </summary>
public struct NetworkConfig
{
    /// <summary>IPv4 address in host byte order.</summary>
    public uint IPAddress;

    /// <summary>Subnet mask in host byte order.</summary>
    public uint SubnetMask;

    /// <summary>Gateway address in host byte order.</summary>
    public uint Gateway;

    /// <summary>Primary DNS server address in host byte order.</summary>
    public uint DnsServer;

    /// <summary>Secondary DNS server address in host byte order (optional).</summary>
    public uint DnsServer2;

    /// <summary>Check if an IP is on the local subnet.</summary>
    public bool IsLocalSubnet(uint ip)
    {
        return (ip & SubnetMask) == (IPAddress & SubnetMask);
    }
}

// Note: IPv4 packet handler delegate removed due to JIT delegate argument limits
// TODO: Add IPv4 handler support when JIT supports more delegate arguments

/// <summary>
/// Received UDP datagram.
/// </summary>
public unsafe struct UdpDatagram
{
    /// <summary>Source IP address (host byte order).</summary>
    public uint SourceIP;

    /// <summary>Source port.</summary>
    public ushort SourcePort;

    /// <summary>Destination port.</summary>
    public ushort DestPort;

    /// <summary>Data buffer.</summary>
    public fixed byte Data[1500];

    /// <summary>Data length.</summary>
    public int Length;

    /// <summary>Whether this slot contains valid data.</summary>
    public bool Valid;
}

/// <summary>
/// Network stack manager - handles Ethernet/ARP/IP processing.
/// </summary>
public unsafe class NetworkStack
{
    // Network device MAC address
    private byte* _macAddress;

    // Network configuration
    private NetworkConfig _config;

    // ARP cache
    private ArpCache _arpCache;

    // Frame buffer for sending
    private byte* _txBuffer;
    private ulong _txBufferPhys;
    private const int TxBufferSize = 1600;

    // Statistics
    private ulong _rxFrames;
    private ulong _txFrames;
    private ulong _arpRequests;
    private ulong _arpReplies;
    private ulong _icmpSent;
    private ulong _icmpReceived;
    private ulong _udpSent;
    private ulong _udpReceived;
    private ulong _tcpSent;
    private ulong _tcpReceived;

    // Phase 7 byte counters (interface IP bytes and TCP payload bytes).
    private ulong _ipBytesIn;
    private ulong _ipBytesOut;
    private ulong _tcpBytesIn;
    private ulong _tcpBytesOut;

    // TCP connections
    private const int MaxTcpConnections = 16;
    private TcpConnection[] _tcpConnections;
    private int _tcpConnectionCount;
    private ushort _nextEphemeralPort;

    // TCP listeners
    private const int MaxTcpListeners = 16;
    private TcpListener?[] _tcpListeners;
    private int _tcpListenerCount;

    // UDP receive queue (simple ring buffer)
    private const int MaxUdpQueueSize = 16;
    private const int MaxUdpDatagramSize = 1500;
    private UdpDatagram[] _udpQueue;
    private int _udpQueueHead;
    private int _udpQueueTail;
    private int _udpQueueCount;

    // Ping tracking
    private ushort _pingIdentifier;
    private ushort _pingSequence;
    private bool _pingPending;
    private uint _pingTargetIP;
    private ulong _pingTimestamp;

    // Pending TX frame length (for TCP auto-responses)
    private int _pendingTxLen;

    /// <summary>
    /// Create a new network stack instance.
    /// </summary>
    /// <param name="macAddress">Pointer to MAC address (6 bytes, must remain valid).</param>
    public NetworkStack(byte* macAddress)
    {
        _macAddress = macAddress;
        _arpCache = new ArpCache();

        // Allocate TX buffer
        _txBufferPhys = Memory.AllocatePages(1);
        _txBuffer = (byte*)Memory.PhysToVirt(_txBufferPhys);

        // Initialize UDP queue
        _udpQueue = new UdpDatagram[MaxUdpQueueSize];
        _udpQueueHead = 0;
        _udpQueueTail = 0;
        _udpQueueCount = 0;

        // Initialize TCP connections
        _tcpConnections = new TcpConnection[MaxTcpConnections];
        _tcpConnectionCount = 0;
        _nextEphemeralPort = 49152; // Start of ephemeral port range

        // Initialize TCP listeners
        _tcpListeners = new TcpListener?[MaxTcpListeners];
        _tcpListenerCount = 0;
    }

    /// <summary>
    /// Configure the network interface.
    /// </summary>
    /// <param name="ipAddress">IPv4 address in host byte order.</param>
    /// <param name="subnetMask">Subnet mask in host byte order.</param>
    /// <param name="gateway">Gateway address in host byte order.</param>
    /// <param name="dnsServer">Primary DNS server (0 to skip).</param>
    /// <param name="dnsServer2">Secondary DNS server (0 to skip).</param>
    public void Configure(uint ipAddress, uint subnetMask, uint gateway,
                          uint dnsServer = 0, uint dnsServer2 = 0)
    {
        _config.IPAddress = ipAddress;
        _config.SubnetMask = subnetMask;
        _config.Gateway = gateway;
        _config.DnsServer = dnsServer;
        _config.DnsServer2 = dnsServer2;

        Debug.Write("[NetStack] Configured IP: ");
        PrintIP(ipAddress);
        Debug.Write(" Mask: ");
        PrintIP(subnetMask);
        Debug.Write(" Gateway: ");
        PrintIP(gateway);
        if (dnsServer != 0)
        {
            Debug.Write(" DNS: ");
            PrintIP(dnsServer);
        }
        Debug.WriteLine();
    }

    /// <summary>
    /// Get the ARP cache.
    /// </summary>
    public ArpCache ArpCache => _arpCache;

    /// <summary>
    /// Get the network configuration.
    /// </summary>
    public NetworkConfig Config => _config;

    /// <summary>
    /// Get the MAC address.
    /// </summary>
    public byte* MacAddress => _macAddress;

    /// <summary>
    /// Process a received Ethernet frame.
    /// </summary>
    /// <param name="data">Pointer to frame data.</param>
    /// <param name="length">Length of frame.</param>
    public void ProcessFrame(byte* data, int length)
    {
        _rxFrames++;

        // Parse Ethernet header
        EthernetFrame frame;
        if (!Ethernet.Parse(data, length, out frame))
        {
            Debug.WriteLine("[NetStack] Failed to parse Ethernet frame");
            return;
        }

        // Check if frame is for us (unicast to our MAC, broadcast, or multicast)
        bool isForUs = Ethernet.CompareMac(frame.DestinationMac, _macAddress) ||
                       Ethernet.IsBroadcast(frame.DestinationMac);

        if (!isForUs)
            return;

        // Dispatch based on EtherType
        switch (frame.EtherType)
        {
            case EtherType.ARP:
                ProcessArp(frame.Payload, frame.PayloadLength, frame.SourceMac);
                break;

            case EtherType.IPv4:
                ProcessIPv4(frame.Payload, frame.PayloadLength, frame.SourceMac);
                break;

            default:
                // Unknown protocol - ignore
                break;
        }
    }

    /// <summary>
    /// Process an ARP packet.
    /// </summary>
    private void ProcessArp(byte* data, int length, byte* senderMac)
    {
        ArpPacket arpPacket;
        if (!ARP.Parse(data, length, out arpPacket))
        {
            Debug.WriteLine("[NetStack] Failed to parse ARP packet");
            return;
        }

        uint senderIP = ARP.GetSenderIP(&arpPacket);
        uint targetIP = ARP.GetTargetIP(&arpPacket);

        // Always update cache with sender info (ARP snooping)
        // Note: SenderMac is a fixed buffer, so we can take its address directly
        _arpCache.Update(senderIP, arpPacket.SenderMac);

        if (arpPacket.Operation == ArpOperation.Request)
        {
            _arpRequests++;

            // Is this request for our IP?
            if (targetIP == _config.IPAddress)
            {
                Debug.Write("[NetStack] ARP request for our IP from ");
                PrintIP(senderIP);
                Debug.WriteLine();

                // Send ARP reply
                SendArpReply(senderIP, senderMac);
            }
        }
        else if (arpPacket.Operation == ArpOperation.Reply)
        {
            _arpReplies++;

            Debug.Write("[NetStack] ARP reply: ");
            PrintIP(senderIP);
            Debug.Write(" is at ");
            PrintMac(senderMac);
            Debug.WriteLine();
        }
    }

    /// <summary>
    /// Process an IPv4 packet.
    /// </summary>
    private void ProcessIPv4(byte* data, int length, byte* ethSrc)
    {
        // Basic IPv4 header validation
        if (length < 20)
            return;

        byte version = (byte)(data[0] >> 4);
        if (version != 4)
            return;

        byte headerLen = (byte)((data[0] & 0x0F) * 4);
        if (headerLen < 20 || headerLen > length)
            return;

        // Extract addresses and protocol
        uint srcIP = ((uint)data[12] << 24) | ((uint)data[13] << 16) |
                     ((uint)data[14] << 8) | data[15];
        uint destIP = ((uint)data[16] << 24) | ((uint)data[17] << 16) |
                      ((uint)data[18] << 8) | data[19];
        byte protocol = data[9];

        // ARP gleaning: learn the sender's MAC from the inbound frame so
        // replies do not get dropped when the ARP cache has no entry yet
        // (e.g. images without the boot-test warmup - the GUI VBox image).
        if (ethSrc != null && srcIP != 0)
        {
            _arpCache.Update(srcIP, ethSrc);
        }

        // Check if packet is for us
        if (destIP != _config.IPAddress && destIP != 0xFFFFFFFF && !IsLocalAddress(destIP))
            return;

        // Use the IPv4 total-length field (bytes 2-3) for the payload size:
        // the received frame may carry trailing Ethernet padding (VirtualBox
        // pads short frames to the 60-byte minimum, QEMU does not), which
        // would otherwise be counted into TCP/UDP checksums and lengths.
        int ipTotalLen = (data[2] << 8) | data[3];
        int effectiveLen = length;
        if (ipTotalLen >= headerLen && ipTotalLen <= length)
            effectiveLen = ipTotalLen;

        // Phase 7: interface-level byte accounting.
        _ipBytesIn += (ulong)effectiveLen;
        if (protocol == TCP.ProtocolNumber)
            _tcpBytesIn += (ulong)(effectiveLen - headerLen);

        // Dispatch based on protocol
        byte* payload = data + headerLen;
        int payloadLen = effectiveLen - headerLen;

        switch (protocol)
        {
            case ICMP.ProtocolNumber:
                ProcessIcmp(payload, payloadLen, srcIP);
                break;

            case UDP.ProtocolNumber:
                ProcessUdp(payload, payloadLen, srcIP, destIP);
                break;

            case TCP.ProtocolNumber:
                ProcessTcp(payload, payloadLen, srcIP, destIP);
                break;

            default:
                Debug.Write("[NetStack] IPv4 packet from ");
                PrintIP(srcIP);
                Debug.Write(" proto=");
                Debug.WriteDecimal(protocol);
                Debug.WriteLine();
                break;
        }
    }

    /// <summary>
    /// Process an ICMP packet.
    /// </summary>
    private void ProcessIcmp(byte* data, int length, uint srcIP)
    {
        _icmpReceived++;

        IcmpPacket packet;
        if (!ICMP.Parse(data, length, out packet))
        {
            Debug.WriteLine("[NetStack] Failed to parse ICMP packet");
            return;
        }

        // Verify checksum
        if (!ICMP.VerifyChecksum(data, length))
        {
            Debug.WriteLine("[NetStack] ICMP checksum invalid");
            return;
        }

        switch (packet.Type)
        {
            case IcmpType.EchoRequest:
                Debug.Write("[NetStack] ICMP Echo Request from ");
                PrintIP(srcIP);
                Debug.Write(" id=");
                Debug.WriteDecimal(packet.Identifier);
                Debug.Write(" seq=");
                Debug.WriteDecimal(packet.Sequence);
                Debug.WriteLine();

                // Send echo reply
                SendEchoReply(srcIP, packet.Identifier, packet.Sequence,
                              packet.Payload, packet.PayloadLength);
                break;

            case IcmpType.EchoReply:
                Debug.Write("[NetStack] ICMP Echo Reply from ");
                PrintIP(srcIP);
                Debug.Write(" id=");
                Debug.WriteDecimal(packet.Identifier);
                Debug.Write(" seq=");
                Debug.WriteDecimal(packet.Sequence);
                Debug.WriteLine();

                // Check if this is our pending ping
                if (_pingPending && srcIP == _pingTargetIP &&
                    packet.Identifier == _pingIdentifier &&
                    packet.Sequence == _pingSequence)
                {
                    _pingPending = false;
                    Debug.WriteLine("[NetStack] Ping reply received!");
                }
                break;

            default:
                Debug.Write("[NetStack] ICMP type=");
                Debug.WriteDecimal(packet.Type);
                Debug.Write(" from ");
                PrintIP(srcIP);
                Debug.WriteLine();
                break;
        }
    }

    /// <summary>
    /// Process a UDP packet.
    /// </summary>
    private void ProcessUdp(byte* data, int length, uint srcIP, uint destIP)
    {
        _udpReceived++;

        UdpPacket packet;
        if (!UDP.Parse(data, length, out packet))
        {
            Debug.WriteLine("[NetStack] Failed to parse UDP packet");
            return;
        }

        // Verify checksum if present
        if (!UDP.VerifyChecksum(data, length, srcIP, destIP))
        {
            Debug.WriteLine("[NetStack] UDP checksum invalid");
            return;
        }

        Debug.Write("[NetStack] UDP from ");
        PrintIP(srcIP);
        Debug.Write(":");
        Debug.WriteDecimal(packet.SourcePort);
        Debug.Write(" to port ");
        Debug.WriteDecimal(packet.DestPort);
        Debug.Write(" len=");
        Debug.WriteDecimal(packet.PayloadLength);
        Debug.WriteLine();

        // Queue the datagram for application processing
        if (_udpQueueCount < MaxUdpQueueSize && packet.PayloadLength <= MaxUdpDatagramSize)
        {
            int idx = _udpQueueTail;
            _udpQueue[idx].SourceIP = srcIP;
            _udpQueue[idx].SourcePort = packet.SourcePort;
            _udpQueue[idx].DestPort = packet.DestPort;
            _udpQueue[idx].Length = packet.PayloadLength;
            _udpQueue[idx].Valid = true;

            // Copy data into the fixed buffer
            fixed (byte* dest = _udpQueue[idx].Data)
            {
                for (int i = 0; i < packet.PayloadLength; i++)
                    dest[i] = packet.Payload[i];
            }

            _udpQueueTail = (_udpQueueTail + 1) % MaxUdpQueueSize;
            _udpQueueCount++;
        }
        else
        {
            Debug.WriteLine("[NetStack] UDP queue full, dropping packet");
        }
    }

    /// <summary>
    /// Send a UDP datagram.
    /// </summary>
    /// <param name="destIP">Destination IP address (host byte order).</param>
    /// <param name="srcPort">Source port.</param>
    /// <param name="destPort">Destination port.</param>
    /// <param name="data">Payload data.</param>
    /// <param name="dataLen">Payload length.</param>
    /// <returns>Frame length, or 0 if MAC unknown (need ARP first).</returns>
    public int SendUdp(uint destIP, ushort srcPort, ushort destPort, byte* data, int dataLen)
    {
        if (dataLen > UDP.MaxPayloadSize)
            return 0;

        // Build UDP packet in a temp buffer
        int maxUdpLen = UdpHeader.Size + dataLen;
        byte* udpBuffer = stackalloc byte[maxUdpLen];
        int udpLen = UDP.BuildPacketWithChecksum(udpBuffer, srcPort, destPort, data, dataLen,
                                                  _config.IPAddress, destIP);
        if (udpLen == 0)
            return 0;

        // Build IPv4 frame with UDP payload
        int frameLen = BuildIPv4Frame(destIP, UDP.ProtocolNumber, udpBuffer, udpLen);
        if (frameLen == 0)
        {
            Debug.Write("[NetStack] UDP to ");
            PrintIP(destIP);
            Debug.WriteLine(" - need ARP first");
            return 0;
        }

        // Queue frame for transmission
        _pendingTxLen = frameLen;
        _udpSent++;

        Debug.Write("[NetStack] Sent UDP to ");
        PrintIP(destIP);
        Debug.Write(":");
        Debug.WriteDecimal(destPort);
        Debug.Write(" len=");
        Debug.WriteDecimal(dataLen);
        Debug.WriteLine();

        return frameLen;
    }

    /// <summary>
    /// Send a UDP broadcast for DHCP (source IP 0.0.0.0, dest IP 255.255.255.255).
    /// Used during DHCP bootstrap when client doesn't have an IP yet.
    /// </summary>
    /// <param name="srcPort">Source port (typically 68 for DHCP client).</param>
    /// <param name="destPort">Destination port (typically 67 for DHCP server).</param>
    /// <param name="data">Payload data.</param>
    /// <param name="dataLen">Payload length.</param>
    /// <returns>Frame length, or 0 on error.</returns>
    public int SendDhcpBroadcast(ushort srcPort, ushort destPort, byte* data, int dataLen)
    {
        if (dataLen > UDP.MaxPayloadSize)
            return 0;

        // Source and destination IPs for DHCP
        uint srcIP = 0;           // 0.0.0.0 - client has no IP yet
        uint destIP = 0xFFFFFFFF; // 255.255.255.255 - broadcast

        // Build UDP packet with checksum
        int maxUdpLen = UdpHeader.Size + dataLen;
        byte* udpBuffer = stackalloc byte[maxUdpLen];
        int udpLen = UDP.BuildPacketWithChecksum(udpBuffer, srcPort, destPort, data, dataLen,
                                                  srcIP, destIP);
        if (udpLen == 0)
            return 0;

        // Build IPv4 header manually (can't use BuildIPv4Frame - it needs ARP)
        int ipLen = 20 + udpLen;  // IPv4 header (no options) + UDP
        byte* ipBuffer = _txBuffer + EthernetHeader.Size;

        // IPv4 header
        ipBuffer[0] = 0x45;  // Version 4, IHL 5 (20 bytes)
        ipBuffer[1] = 0x00;  // DSCP/ECN
        ipBuffer[2] = (byte)(ipLen >> 8);   // Total length
        ipBuffer[3] = (byte)ipLen;
        ipBuffer[4] = 0x00;  // Identification
        ipBuffer[5] = 0x00;
        ipBuffer[6] = 0x00;  // Flags/Fragment offset
        ipBuffer[7] = 0x00;
        ipBuffer[8] = 64;    // TTL
        ipBuffer[9] = UDP.ProtocolNumber;  // Protocol: UDP
        ipBuffer[10] = 0x00; // Checksum (calculated below)
        ipBuffer[11] = 0x00;
        // Source IP: 0.0.0.0
        ipBuffer[12] = 0;
        ipBuffer[13] = 0;
        ipBuffer[14] = 0;
        ipBuffer[15] = 0;
        // Dest IP: 255.255.255.255
        ipBuffer[16] = 255;
        ipBuffer[17] = 255;
        ipBuffer[18] = 255;
        ipBuffer[19] = 255;

        // Calculate IP header checksum
        uint sum = 0;
        for (int i = 0; i < 20; i += 2)
            sum += (uint)((ipBuffer[i] << 8) | ipBuffer[i + 1]);
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        ushort ipChecksum = (ushort)~sum;
        ipBuffer[10] = (byte)(ipChecksum >> 8);
        ipBuffer[11] = (byte)ipChecksum;

        // Copy UDP data after IPv4 header
        for (int i = 0; i < udpLen; i++)
            ipBuffer[20 + i] = udpBuffer[i];

        // Build Ethernet header with broadcast MAC
        byte* broadcastMac = stackalloc byte[6];
        Ethernet.SetBroadcast(broadcastMac);
        int ethLen = Ethernet.BuildHeader(_txBuffer, broadcastMac, _macAddress, EtherType.IPv4);

        int totalLen = EthernetHeader.Size + ipLen;

        // Pad to minimum frame size
        if (totalLen < EthernetHeader.MinFrameSize)
        {
            for (int i = totalLen; i < EthernetHeader.MinFrameSize; i++)
                _txBuffer[i] = 0;
            totalLen = EthernetHeader.MinFrameSize;
        }

        _pendingTxLen = totalLen;
        _udpSent++;

        Debug.Write("[NetStack] DHCP broadcast to port ");
        Debug.WriteDecimal(destPort);
        Debug.Write(" len=");
        Debug.WriteDecimal(dataLen);
        Debug.WriteLine();

        return totalLen;
    }

    /// <summary>
    /// Check if there are UDP datagrams available to receive.
    /// </summary>
    public int UdpAvailable() => _udpQueueCount;

    /// <summary>
    /// Scans the UDP queue for the first datagram matching the given
    /// source/destination ports, copies its payload into
    /// <paramref name="buffer"/> and consumes it (non-matching
    /// datagrams are consumed too). Returns the payload length, or 0
    /// when no matching datagram was queued.
    ///
    /// Note: all comparisons happen on plain field reads inside this
    /// method - a DHCP client that compared out-parameter ports across
    /// call boundaries mis-compiled under the Tier-0 JIT (matching
    /// values, branch not taken).
    /// </summary>
    public int ReceiveUdpMatching(ushort wantSrcPort, ushort wantDestPort,
                                  byte* buffer, int bufferLen)
    {
        int wantSrc = wantSrcPort;
        int wantDest = wantDestPort;

        for (int guard = 0; guard < MaxUdpQueueSize; guard++)
        {
            int count = _udpQueueCount;
            if (count == 0)
                return 0;

            int idx = _udpQueueHead;
            if (!_udpQueue[idx].Valid)
                return 0;

            int src = _udpQueue[idx].SourcePort;
            int dest = _udpQueue[idx].DestPort;
            int len = _udpQueue[idx].Length;

            bool match = src == wantSrc;
            if (match)
                match = dest == wantDest;

            // Consume the entry.
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
                return copyLen;
            }
        }
        return 0;
    }

    /// <summary>
    /// Receive a UDP datagram from the queue.
    /// </summary>
    /// <param name="srcIP">Receives source IP address.</param>
    /// <param name="srcPort">Receives source port.</param>
    /// <param name="destPort">Receives destination port.</param>
    /// <param name="buffer">Buffer to receive data.</param>
    /// <param name="bufferLen">Buffer size.</param>
    /// <returns>Number of bytes received, or 0 if no data available.</returns>
    public int ReceiveUdp(out uint srcIP, out ushort srcPort, out ushort destPort,
                          byte* buffer, int bufferLen)
    {
        srcIP = 0;
        srcPort = 0;
        destPort = 0;

        if (_udpQueueCount == 0)
            return 0;

        int idx = _udpQueueHead;
        if (!_udpQueue[idx].Valid)
            return 0;

        srcIP = _udpQueue[idx].SourceIP;
        srcPort = _udpQueue[idx].SourcePort;
        destPort = _udpQueue[idx].DestPort;
        int dataLen = _udpQueue[idx].Length;

        // Copy data from the fixed buffer
        int copyLen = dataLen < bufferLen ? dataLen : bufferLen;
        fixed (byte* src = _udpQueue[idx].Data)
        {
            for (int i = 0; i < copyLen; i++)
                buffer[i] = src[i];
        }

        // Mark slot as empty
        _udpQueue[idx].Valid = false;
        _udpQueueHead = (_udpQueueHead + 1) % MaxUdpQueueSize;
        _udpQueueCount--;

        return copyLen;
    }

    // ===========================================
    // TCP Methods
    // ===========================================

    /// <summary>
    /// When true, per-packet RX/TX trace lines are suppressed. Defaults to
    /// true: every trace line costs ~0.5 ms of 115200-baud serial time and
    /// fires per packet, which measurably inflates connection handshakes
    /// (TLS/SSH latency probes) apart from flooding production logs.
    /// Debugging re-enables traces by clearing this flag on the stack.
    /// </summary>
    public bool Quiet = true;

    /// <summary>
    /// Process a TCP packet.
    /// </summary>
    private void ProcessTcp(byte* data, int length, uint srcIP, uint destIP)
    {
        _tcpReceived++;

        TcpPacket packet;
        if (!TCP.Parse(data, length, out packet))
        {
            if (!Quiet)
                Debug.WriteLine("[NetStack] Failed to parse TCP packet");
            return;
        }

        // Verify checksum
        if (!TCP.VerifyChecksum(data, length, srcIP, destIP))
        {
            Debug.Write("[NetStack] TCP cksum fail: sum=");
            Debug.WriteHex((uint)TCP.ChecksumSum(data, length, srcIP, destIP));
            Debug.Write(" stored=");
            Debug.WriteHex((uint)((data[16] << 8) | data[17]));
            Debug.Write(" len=");
            Debug.WriteDecimal(length);
            Debug.Write(" doff=");
            Debug.WriteHex((uint)((data[12] >> 4) & 0xF));
            Debug.WriteLine();
            return;
        }

        if (!Quiet)
        {
            Debug.Write("[NetStack] TCP from ");
            PrintIP(srcIP);
            Debug.Write(":");
            Debug.WriteDecimal(packet.SourcePort);
            Debug.Write(" to port ");
            Debug.WriteDecimal(packet.DestPort);
            Debug.Write(" flags=");
            PrintTcpFlags(packet.Flags);
            Debug.Write(" seq=");
            Debug.WriteHex((uint)packet.SeqNum);
            Debug.Write(" ack=");
            Debug.WriteHex((uint)packet.AckNum);
            Debug.WriteLine();
        }

        // Find matching connection
        TcpConnection conn = FindConnection(srcIP, packet.SourcePort, packet.DestPort);

        if (conn == null)
        {
            // Phase 7 loopback healing: connections created against a
            // loopback destination store the remote as 127.x, while the
            // peer's frames arrive with the interface's own IP as source
            // (loopback delivery rewrites neither header). Match the port
            // pair in that case.
            if (IsLocalAddress(srcIP))
            {
                for (int i = 0; i < MaxTcpConnections && conn == null; i++)
                {
                    var c = _tcpConnections[i];
                    if (c == null)
                        continue;
                    if (c.LocalEndpoint.Port == packet.DestPort &&
                        c.RemoteEndpoint.Port == packet.SourcePort &&
                        IsLocalAddress(c.RemoteEndpoint.IP) &&
                        c.RemoteEndpoint.IP != srcIP)
                    {
                        conn = c;
                    }
                }
            }
        }

        if (conn == null)
        {
            // Check if this is a SYN to a listening port
            if (packet.IsSyn && !packet.IsAck)
            {
                var listener = FindListener(packet.DestPort);
                if (listener != null)
                {
                    listener.HandleIncomingSyn(srcIP, packet.SourcePort, packet.DestPort,
                                                packet.SeqNum, packet.Window, data, length);
                    return;
                }
            }

            // No connection or listener found
            Debug.WriteLine("[NetStack] No TCP connection found for packet");
            return;
        }

        // Process packet through connection state machine
        byte* responseBuffer = stackalloc byte[TcpHeader.MaxSize + 64];
        int responseLen = conn.ProcessPacket(&packet, responseBuffer);

        if (responseLen > 0)
        {
            // Reply to the peer address recorded on the connection: on
            // loopback the peer's frames arrive with the interface IP as
            // source while the connection records 127.x, and the TCP
            // checksum was computed against the recorded address. For
            // ordinary traffic the two are identical.
            int frameLen = BuildIPv4Frame(conn.RemoteEndpoint.IP, TCP.ProtocolNumber, responseBuffer, responseLen);
            if (frameLen > 0)
            {
                _pendingTxLen = frameLen;
                _tcpSent++;
            }
        }
    }

    /// <summary>
    /// Get and clear pending TX frame length (set by TCP auto-responses).
    /// Returns 0 if no pending frame.
    /// </summary>
    public int GetPendingTxLen()
    {
        int len = _pendingTxLen;
        _pendingTxLen = 0;
        return len;
    }

    /// <summary>
    /// Find a TCP connection matching the given parameters.
    /// Scans every slot: removed connections leave gaps in the table, so
    /// the slot range can extend past _tcpConnectionCount.
    /// </summary>
    private TcpConnection FindConnection(uint remoteIP, ushort remotePort, ushort localPort)
    {
        for (int i = 0; i < MaxTcpConnections; i++)
        {
            var conn = _tcpConnections[i];
            if (conn != null && conn.Matches(remoteIP, remotePort, localPort))
                return conn;
        }
        return null;
    }

    /// <summary>
    /// Add a TCP connection to the connection table.
    /// </summary>
    private int AddConnection(TcpConnection conn)
    {
        if (_tcpConnectionCount >= MaxTcpConnections)
            return -1;

        // Find empty slot
        for (int i = 0; i < MaxTcpConnections; i++)
        {
            if (_tcpConnections[i] == null)
            {
                _tcpConnections[i] = conn;
                conn.StackIndex = i;
                _tcpConnectionCount++;
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Remove a TCP connection from the connection table.
    /// </summary>
    private void RemoveConnection(int index)
    {
        if (index >= 0 && index < MaxTcpConnections && _tcpConnections[index] != null)
        {
            _tcpConnections[index].StackIndex = -1;
            _tcpConnections[index] = null;
            _tcpConnectionCount--;
        }
    }

    /// <summary>
    /// Remove connections that have fully closed - or have been stuck in
    /// a closing state for over 30 seconds - from the connection table.
    /// Server loops call this after pumping so finished sessions release
    /// slots promptly and a restarted server can bind immediately
    /// (Phase 6; this is the simplified stand-in for TIME_WAIT aging).
    /// </summary>
    public void ReapClosedConnections()
    {
        long now = (long)Timer.GetUptimeMilliseconds();
        for (int i = 0; i < MaxTcpConnections; i++)
        {
            var conn = _tcpConnections[i];
            if (conn == null)
                continue;

            bool reap = conn.IsClosed;
            if (!reap)
            {
                var st = conn.State;
                if ((st == TcpState.FinWait1 || st == TcpState.FinWait2 ||
                     st == TcpState.Closing || st == TcpState.LastAck ||
                     st == TcpState.TimeWait) &&
                    conn.LastActivityMs > 0 && now - conn.LastActivityMs > 30000)
                {
                    reap = true;
                }
            }

            if (reap)
                RemoveConnection(i);
        }
    }

    /// <summary>
    /// Allocate an ephemeral port.
    /// </summary>
    private ushort AllocateEphemeralPort()
    {
        ushort port = _nextEphemeralPort;
        _nextEphemeralPort++;
        if (_nextEphemeralPort < 49152)
            _nextEphemeralPort = 49152;
        return port;
    }

    /// <summary>
    /// Initiate a TCP connection.
    /// </summary>
    /// <param name="destIP">Destination IP address (host byte order).</param>
    /// <param name="destPort">Destination port.</param>
    /// <returns>Connection index, or -1 on error. Returns -2 if ARP needed.</returns>
    public int TcpConnect(uint destIP, ushort destPort)
    {
        if (_tcpConnectionCount >= MaxTcpConnections)
        {
            Debug.WriteLine("[NetStack] TCP connection table full");
            return -1;
        }

        ushort localPort = AllocateEphemeralPort();
        var conn = new TcpConnection(_config.IPAddress, localPort, destIP, destPort);

        int index = AddConnection(conn);
        if (index < 0)
            return -1;

        // Build SYN packet
        byte* synBuffer = stackalloc byte[TcpHeader.MinSize];
        int synLen = conn.InitiateConnect(synBuffer);
        if (synLen == 0)
        {
            RemoveConnection(index);
            return -1;
        }

        // Send SYN
        int frameLen = BuildIPv4Frame(destIP, TCP.ProtocolNumber, synBuffer, synLen);
        if (frameLen == 0)
        {
            // Need ARP resolution
            Debug.Write("[NetStack] TCP connect to ");
            PrintIP(destIP);
            Debug.WriteLine(" - need ARP first");
            RemoveConnection(index);
            return -2;
        }

        _pendingTxLen = frameLen;
        _tcpSent++;

        Debug.Write("[NetStack] TCP connecting to ");
        PrintIP(destIP);
        Debug.Write(":");
        Debug.WriteDecimal(destPort);
        Debug.Write(" from port ");
        Debug.WriteDecimal(localPort);
        Debug.WriteLine();

        return index;
    }

    /// <summary>
    /// Get a TCP connection by index.
    /// </summary>
    public TcpConnection GetTcpConnection(int index)
    {
        if (index >= 0 && index < MaxTcpConnections)
            return _tcpConnections[index];
        return null;
    }

    /// <summary>
    /// Get the maximum number of TCP connection slots.
    /// </summary>
    public int TcpConnectionSlots => MaxTcpConnections;

    /// <summary>
    /// Get the number of active TCP connections.
    /// </summary>
    public int TcpConnectionCount => _tcpConnectionCount;

    // ===========================================
    // TCP Listener Methods
    // ===========================================

    /// <summary>
    /// Register a TCP listener for incoming connections.
    /// </summary>
    /// <param name="listener">The listener to register.</param>
    /// <returns>True if registered successfully.</returns>
    public bool RegisterListener(TcpListener listener, bool reuseAddress = false)
    {
        if (_tcpListenerCount >= MaxTcpListeners)
            return false;

        // Check for duplicate port. SO_REUSEADDR semantics: allow the new
        // listener to take over the port, replacing a stale registration
        // (e.g. a previous server instance), so servers can restart
        // without the old bind blocking them.
        for (int i = 0; i < MaxTcpListeners; i++)
        {
            if (_tcpListeners[i] != null && _tcpListeners[i]!.Port == listener.Port)
            {
                if (!reuseAddress)
                {
                    Debug.WriteLine("[NetStack] Port already in use by another listener");
                    return false;
                }
                Debug.WriteLine("[NetStack] SO_REUSEADDR: replacing existing listener for port");
                _tcpListeners[i] = listener;
                return true;
            }
        }

        // Find empty slot
        for (int i = 0; i < MaxTcpListeners; i++)
        {
            if (_tcpListeners[i] == null)
            {
                _tcpListeners[i] = listener;
                _tcpListenerCount++;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Unregister a TCP listener.
    /// </summary>
    /// <param name="listener">The listener to unregister.</param>
    public void UnregisterListener(TcpListener listener)
    {
        for (int i = 0; i < MaxTcpListeners; i++)
        {
            if (_tcpListeners[i] == listener)
            {
                _tcpListeners[i] = null;
                _tcpListenerCount--;
                break;
            }
        }
    }

    /// <summary>
    /// Find a listener for the given local port.
    /// </summary>
    private TcpListener? FindListener(ushort localPort)
    {
        for (int i = 0; i < MaxTcpListeners; i++)
        {
            if (_tcpListeners[i] != null && _tcpListeners[i]!.Port == localPort)
                return _tcpListeners[i];
        }
        return null;
    }

    /// <summary>
    /// Add a connection created by a listener (for tracking server-side connections).
    /// </summary>
    /// <param name="conn">The connection to add.</param>
    /// <returns>True if added successfully.</returns>
    public bool AddListenerConnection(TcpConnection conn)
    {
        return AddConnection(conn) >= 0;
    }

    /// <summary>
    /// Queue a pending TX frame length for transmission.
    /// Used by listeners to send SYN-ACK responses.
    /// </summary>
    /// <param name="frameLen">The frame length to queue.</param>
    public void QueuePendingTx(int frameLen)
    {
        _pendingTxLen = frameLen;
        _tcpSent++;
    }

    /// <summary>
    /// Get the number of active TCP listeners.
    /// </summary>
    public int TcpListenerCount => _tcpListenerCount;

    /// <summary>
    /// Send data on a TCP connection.
    /// </summary>
    /// <param name="connIndex">Connection index from TcpConnect.</param>
    /// <param name="data">Data to send.</param>
    /// <param name="length">Data length.</param>
    /// <returns>Number of bytes sent, or 0 on error.</returns>
    public int TcpSend(int connIndex, byte* data, int length)
    {
        var conn = GetTcpConnection(connIndex);
        if (conn == null || !conn.IsConnected)
            return 0;

        // Build data packet
        int maxTcpLen = TcpHeader.MinSize + length;
        byte* tcpBuffer = stackalloc byte[maxTcpLen];
        int tcpLen = conn.BuildDataPacket(tcpBuffer, data, length);
        if (tcpLen == 0)
            return 0;

        // Send packet
        int frameLen = BuildIPv4Frame(conn.RemoteEndpoint.IP, TCP.ProtocolNumber, tcpBuffer, tcpLen);
        if (frameLen == 0)
            return 0;

        // Queue frame for transmission
        _pendingTxLen = frameLen;
        _tcpSent++;

        // Per-segment trace: at 115200 baud this costs ~4 ms per segment
        // and throttles bulk transfers, so it is suppressed by Quiet (the
        // benchmark harness enables it for the transfer window).
        if (!Quiet)
        {
            Debug.Write("[NetStack] TCP sent ");
            Debug.WriteDecimal(length);
            Debug.Write(" bytes to ");
            PrintIP(conn.RemoteEndpoint.IP);
            Debug.Write(":");
            Debug.WriteDecimal(conn.RemoteEndpoint.Port);
            Debug.WriteLine();
        }

        return length;
    }

    /// <summary>
    /// Receive data from a TCP connection.
    /// </summary>
    /// <param name="connIndex">Connection index from TcpConnect.</param>
    /// <param name="buffer">Buffer to receive data.</param>
    /// <param name="maxLength">Maximum bytes to read.</param>
    /// <returns>Number of bytes read, or 0 if no data available.</returns>
    public int TcpReceive(int connIndex, byte* buffer, int maxLength)
    {
        var conn = GetTcpConnection(connIndex);
        if (conn == null)
            return 0;

        return conn.Read(buffer, maxLength);
    }

    /// <summary>
    /// Check how many bytes are available to read on a TCP connection.
    /// </summary>
    public int TcpAvailable(int connIndex)
    {
        var conn = GetTcpConnection(connIndex);
        if (conn == null)
            return 0;
        return conn.Available;
    }

    /// <summary>
    /// Close a TCP connection.
    /// </summary>
    /// <param name="connIndex">Connection index from TcpConnect.</param>
    /// <returns>True if close initiated.</returns>
    public bool TcpClose(int connIndex)
    {
        var conn = GetTcpConnection(connIndex);
        if (conn == null)
            return false;

        if (conn.IsClosed)
        {
            RemoveConnection(connIndex);
            return true;
        }

        // Build FIN packet
        byte* finBuffer = stackalloc byte[TcpHeader.MinSize];
        int finLen = conn.InitiateClose(finBuffer);
        if (finLen == 0)
        {
            // Already closing or closed
            if (conn.IsClosed)
                RemoveConnection(connIndex);
            return true;
        }

        // Send FIN
        int frameLen = BuildIPv4Frame(conn.RemoteEndpoint.IP, TCP.ProtocolNumber, finBuffer, finLen);
        if (frameLen > 0)
        {
            _pendingTxLen = frameLen;  // Queue frame for transmission
            _tcpSent++;
            Debug.Write("[NetStack] TCP closing connection to ");
            PrintIP(conn.RemoteEndpoint.IP);
            Debug.Write(":");
            Debug.WriteDecimal(conn.RemoteEndpoint.Port);
            Debug.WriteLine();
        }

        return true;
    }

    /// <summary>
    /// Get TCP statistics.
    /// </summary>
    /// <summary>
    /// Phase 7: byte counters. ipIn/ipOut count IPv4 bytes at this
    /// interface; tcpIn/tcpOut count TCP payload bytes (both directions
    /// include TCP headers for RX/TX respectively - see the call sites).
    /// </summary>
    public void GetByteStats(out ulong ipIn, out ulong ipOut, out ulong tcpIn, out ulong tcpOut)
    {
        ipIn = _ipBytesIn;
        ipOut = _ipBytesOut;
        tcpIn = _tcpBytesIn;
        tcpOut = _tcpBytesOut;
    }

    public void GetTcpStats(out ulong sent, out ulong received, out int activeConnections)
    {
        sent = _tcpSent;
        received = _tcpReceived;
        activeConnections = _tcpConnectionCount;
    }

    /// <summary>
    /// Half-close a connection: send a FIN for the local send direction
    /// while the receive direction stays open (FIN_WAIT1). Used by the
    /// Phase 6 socket Shutdown API.
    /// </summary>
    public bool TcpShutdownSend(int connIndex)
    {
        var conn = GetTcpConnection(connIndex);
        if (conn == null)
            return false;

        byte* finBuffer = stackalloc byte[TcpHeader.MinSize];
        int finLen = conn.InitiateClose(finBuffer);
        if (finLen == 0)
            return false;

        int frameLen = BuildIPv4Frame(conn.RemoteEndpoint.IP, TCP.ProtocolNumber, finBuffer, finLen);
        if (frameLen > 0)
        {
            _pendingTxLen = frameLen;
            _tcpSent++;
        }
        return true;
    }

    /// <summary>
    /// Print TCP flags.
    /// </summary>
    private void PrintTcpFlags(byte flags)
    {
        if ((flags & TcpFlags.SYN) != 0) Debug.Write("S");
        if ((flags & TcpFlags.ACK) != 0) Debug.Write("A");
        if ((flags & TcpFlags.FIN) != 0) Debug.Write("F");
        if ((flags & TcpFlags.RST) != 0) Debug.Write("R");
        if ((flags & TcpFlags.PSH) != 0) Debug.Write("P");
        if ((flags & TcpFlags.URG) != 0) Debug.Write("U");
    }

    /// <summary>
    /// Send an ICMP Echo Request (ping).
    /// </summary>
    /// <param name="destIP">Destination IP address (host byte order).</param>
    /// <returns>Frame length, or 0 if MAC unknown (need ARP first).</returns>
    public int SendPing(uint destIP)
    {
        // Increment sequence for each ping
        _pingSequence++;

        // Use a fixed identifier for this session
        if (_pingIdentifier == 0)
            _pingIdentifier = 0x1234;

        // Build ICMP Echo Request in a temp buffer
        byte* icmpBuffer = stackalloc byte[64];
        int icmpLen = ICMP.BuildEchoRequest(icmpBuffer, _pingIdentifier, _pingSequence, null, 0);
        if (icmpLen == 0)
            return 0;

        // Build IPv4 frame with ICMP payload
        int frameLen = BuildIPv4Frame(destIP, ICMP.ProtocolNumber, icmpBuffer, icmpLen);
        if (frameLen == 0)
        {
            // Need ARP resolution
            Debug.Write("[NetStack] Ping to ");
            PrintIP(destIP);
            Debug.WriteLine(" - need ARP first");
            return 0;
        }

        // Track pending ping
        _pingPending = true;
        _pingTargetIP = destIP;
        _icmpSent++;

        Debug.Write("[NetStack] Sent ping to ");
        PrintIP(destIP);
        Debug.Write(" id=");
        Debug.WriteDecimal(_pingIdentifier);
        Debug.Write(" seq=");
        Debug.WriteDecimal(_pingSequence);
        Debug.WriteLine();

        return frameLen;
    }

    /// <summary>
    /// Send an ICMP Echo Reply.
    /// </summary>
    private int SendEchoReply(uint destIP, ushort identifier, ushort sequence,
                               byte* payload, int payloadLen)
    {
        // Build ICMP Echo Reply in a temp buffer
        int maxIcmpLen = IcmpEchoHeader.Size + payloadLen;
        byte* icmpBuffer = stackalloc byte[maxIcmpLen];
        int icmpLen = ICMP.BuildEchoReply(icmpBuffer, identifier, sequence, payload, payloadLen);
        if (icmpLen == 0)
            return 0;

        // Build IPv4 frame with ICMP payload
        int frameLen = BuildIPv4Frame(destIP, ICMP.ProtocolNumber, icmpBuffer, icmpLen);
        if (frameLen == 0)
        {
            Debug.Write("[NetStack] Echo reply to ");
            PrintIP(destIP);
            Debug.WriteLine(" - need ARP first");
            return 0;
        }

        _icmpSent++;

        Debug.Write("[NetStack] Sent echo reply to ");
        PrintIP(destIP);
        Debug.WriteLine();

        return frameLen;
    }

    /// <summary>
    /// Check if a ping is pending.
    /// </summary>
    public bool IsPingPending() => _pingPending;

    /// <summary>
    /// Get ping statistics.
    /// </summary>
    public void GetPingStats(out ushort identifier, out ushort sequence, out bool pending)
    {
        identifier = _pingIdentifier;
        sequence = _pingSequence;
        pending = _pingPending;
    }

    /// <summary>
    /// Send an ARP request.
    /// </summary>
    /// <param name="targetIP">IP address to resolve (host byte order).</param>
    /// <returns>Frame length sent, or 0 on error.</returns>
    public int SendArpRequest(uint targetIP)
    {
        // Build ARP request
        byte* arpData = _txBuffer + EthernetHeader.Size;
        int arpLen = ARP.BuildRequest(arpData, _macAddress, _config.IPAddress, targetIP);
        if (arpLen == 0)
            return 0;

        // Build Ethernet frame (broadcast)
        byte* broadcastMac = stackalloc byte[6];
        Ethernet.SetBroadcast(broadcastMac);

        int frameLen = Ethernet.BuildHeader(_txBuffer, broadcastMac, _macAddress, EtherType.ARP);
        if (frameLen == 0)
            return 0;

        // Total frame length
        int totalLen = EthernetHeader.Size + arpLen;

        // Pad to minimum frame size
        if (totalLen < EthernetHeader.MinFrameSize)
        {
            for (int i = totalLen; i < EthernetHeader.MinFrameSize; i++)
                _txBuffer[i] = 0;
            totalLen = EthernetHeader.MinFrameSize;
        }

        Debug.Write("[NetStack] Sending ARP request for ");
        PrintIP(targetIP);
        Debug.WriteLine();

        _txFrames++;
        return totalLen;
    }

    /// <summary>
    /// Get the TX buffer after building a frame.
    /// </summary>
    public byte* GetTxBuffer() => _txBuffer;

    /// <summary>
    /// Send an ARP reply.
    /// </summary>
    private void SendArpReply(uint targetIP, byte* targetMac)
    {
        // Build ARP reply
        byte* arpData = _txBuffer + EthernetHeader.Size;
        int arpLen = ARP.BuildReply(arpData, _macAddress, _config.IPAddress, targetMac, targetIP);
        if (arpLen == 0)
            return;

        // Build Ethernet frame (unicast to requester)
        Ethernet.BuildHeader(_txBuffer, targetMac, _macAddress, EtherType.ARP);

        // Total frame length
        int totalLen = EthernetHeader.Size + arpLen;
        if (totalLen < EthernetHeader.MinFrameSize)
            totalLen = EthernetHeader.MinFrameSize;

        _txFrames++;

        Debug.Write("[NetStack] Sending ARP reply to ");
        PrintIP(targetIP);
        Debug.WriteLine();

        // Note: Caller needs to actually transmit _txBuffer
    }

    /// <summary>
    /// Phase 7: true when <paramref name="ip"/> is owned by this host
    /// (the interface address or the 127.0.0.0/8 loopback range). Local
    /// destinations are routed back through <see cref="ProcessFrame"/> by
    /// NetworkPump instead of the NIC.
    /// </summary>
    public bool IsLocalAddress(uint ip)
    {
        return ip == _config.IPAddress || (ip >> 24) == 127;
    }

    /// <summary>
    /// Resolve an IP address to a MAC address.
    /// </summary>
    /// <param name="ip">IP address to resolve (host byte order).</param>
    /// <param name="mac">Buffer to receive MAC address (6 bytes).</param>
    /// <returns>True if resolved from cache, false if ARP request needed.</returns>
    public bool ResolveIP(uint ip, byte* mac)
    {
        // Check if on local subnet
        uint nextHop = _config.IsLocalSubnet(ip) ? ip : _config.Gateway;

        // Look up in cache
        return _arpCache.Lookup(nextHop, mac);
    }

    /// <summary>
    /// Build an IPv4 frame for transmission.
    /// </summary>
    /// <param name="destIP">Destination IP (host byte order).</param>
    /// <param name="protocol">IP protocol number.</param>
    /// <param name="payload">Payload data.</param>
    /// <param name="payloadLen">Payload length.</param>
    /// <returns>Frame length, or 0 if MAC unknown (need ARP).</returns>
    public int BuildIPv4Frame(uint destIP, byte protocol, byte* payload, int payloadLen)
    {
        // Determine next hop
        uint nextHop = _config.IsLocalSubnet(destIP) ? destIP : _config.Gateway;

        // Resolve MAC address (loopback destinations use our own MAC).
        byte* destMac = stackalloc byte[6];
        if (IsLocalAddress(destIP))
        {
            for (int i = 0; i < 6; i++)
                destMac[i] = _macAddress[i];
        }
        else if (!_arpCache.Lookup(nextHop, destMac))
        {
            // Send an ARP request so a retry can resolve the next hop.
            // (The packet itself is dropped - callers retransmit.)
            SendArpRequest(nextHop);
            return 0;
        }

        // Build IP header
        int ipHeaderLen = 20;
        int totalIpLen = ipHeaderLen + payloadLen;

        // Phase 7: interface-level byte accounting.
        _ipBytesOut += (ulong)totalIpLen;
        if (protocol == TCP.ProtocolNumber)
            _tcpBytesOut += (ulong)payloadLen;

        byte* ipHeader = _txBuffer + EthernetHeader.Size;

        // Version (4) and header length (5 = 20 bytes)
        ipHeader[0] = 0x45;
        // DSCP/ECN
        ipHeader[1] = 0;
        // Total length (big-endian)
        ipHeader[2] = (byte)(totalIpLen >> 8);
        ipHeader[3] = (byte)totalIpLen;
        // Identification
        ipHeader[4] = 0;
        ipHeader[5] = 0;
        // Flags and fragment offset
        ipHeader[6] = 0x40; // Don't fragment
        ipHeader[7] = 0;
        // TTL
        ipHeader[8] = 64;
        // Protocol
        ipHeader[9] = protocol;
        // Header checksum (initially zero, calculate after)
        ipHeader[10] = 0;
        ipHeader[11] = 0;
        // Source IP
        ipHeader[12] = (byte)(_config.IPAddress >> 24);
        ipHeader[13] = (byte)(_config.IPAddress >> 16);
        ipHeader[14] = (byte)(_config.IPAddress >> 8);
        ipHeader[15] = (byte)_config.IPAddress;
        // Destination IP
        ipHeader[16] = (byte)(destIP >> 24);
        ipHeader[17] = (byte)(destIP >> 16);
        ipHeader[18] = (byte)(destIP >> 8);
        ipHeader[19] = (byte)destIP;

        // Calculate header checksum
        uint sum = 0;
        for (int i = 0; i < ipHeaderLen; i += 2)
        {
            sum += (uint)((ipHeader[i] << 8) | ipHeader[i + 1]);
        }
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        ushort checksum = (ushort)~sum;
        ipHeader[10] = (byte)(checksum >> 8);
        ipHeader[11] = (byte)checksum;

        // Copy payload
        byte* payloadDest = ipHeader + ipHeaderLen;
        for (int i = 0; i < payloadLen; i++)
            payloadDest[i] = payload[i];

        // Build Ethernet header
        Ethernet.BuildHeader(_txBuffer, destMac, _macAddress, EtherType.IPv4);

        int totalLen = EthernetHeader.Size + totalIpLen;
        if (totalLen < EthernetHeader.MinFrameSize)
        {
            for (int i = totalLen; i < EthernetHeader.MinFrameSize; i++)
                _txBuffer[i] = 0;
            totalLen = EthernetHeader.MinFrameSize;
        }

        _txFrames++;
        return totalLen;
    }

    /// <summary>
    /// Print an IP address.
    /// </summary>
    private void PrintIP(uint ip)
    {
        Debug.WriteDecimal((ip >> 24) & 0xFF);
        Debug.Write(".");
        Debug.WriteDecimal((ip >> 16) & 0xFF);
        Debug.Write(".");
        Debug.WriteDecimal((ip >> 8) & 0xFF);
        Debug.Write(".");
        Debug.WriteDecimal(ip & 0xFF);
    }

    /// <summary>
    /// Print a MAC address.
    /// </summary>
    private void PrintMac(byte* mac)
    {
        for (int i = 0; i < 6; i++)
        {
            Debug.WriteHex(mac[i]);
            if (i < 5) Debug.Write(":");
        }
    }

    /// <summary>
    /// Get statistics.
    /// </summary>
    public void GetStats(out ulong rxFrames, out ulong txFrames, out ulong arpReq, out ulong arpRep)
    {
        rxFrames = _rxFrames;
        txFrames = _txFrames;
        arpReq = _arpRequests;
        arpRep = _arpReplies;
    }

    /// <summary>
    /// Get ICMP statistics.
    /// </summary>
    public void GetIcmpStats(out ulong sent, out ulong received)
    {
        sent = _icmpSent;
        received = _icmpReceived;
    }

    /// <summary>
    /// Get UDP statistics.
    /// </summary>
    public void GetUdpStats(out ulong sent, out ulong received)
    {
        sent = _udpSent;
        received = _udpReceived;
    }
}
