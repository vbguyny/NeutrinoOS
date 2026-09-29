// ProtonOS DDK - DNS Resolver
// High-level DNS resolver with timeout and retry support.

using System;
using ProtonOS.DDK.Kernel;

namespace ProtonOS.DDK.Network.Stack;

/// <summary>
/// DNS resolver for hostname-to-IP resolution.
/// Uses the network stack's UDP API to communicate with DNS servers.
/// </summary>
public unsafe class DnsResolver
{
    private NetworkStack _stack;
    private ushort _nextTransactionId;

    /// <summary>
    /// Create a new DNS resolver.
    /// </summary>
    /// <param name="stack">Network stack to use for communication.</param>
    public DnsResolver(NetworkStack stack)
    {
        _stack = stack;
        // Initialize transaction ID with some randomness based on uptime
        _nextTransactionId = (ushort)(Timer.GetUptimeMilliseconds() & 0xFFFF);
    }

    /// <summary>
    /// Delegate for transmitting a frame.
    /// </summary>
    public delegate void TransmitFrameDelegate(byte* data, int length);

    /// <summary>
    /// Delegate for receiving a frame.
    /// </summary>
    public delegate int ReceiveFrameDelegate(byte* buffer, int maxLength);

    /// <summary>
    /// Resolve a hostname to an IPv4 address.
    /// </summary>
    /// <param name="hostname">Hostname to resolve (null-terminated or with explicit length).</param>
    /// <param name="hostnameLen">Length of hostname.</param>
    /// <param name="timeoutMs">Timeout in milliseconds.</param>
    /// <param name="transmit">Delegate to transmit frames.</param>
    /// <param name="receive">Delegate to receive frames.</param>
    /// <returns>IPv4 address in host byte order, or 0 on failure.</returns>
    public uint Resolve(byte* hostname, int hostnameLen, int timeoutMs,
                        TransmitFrameDelegate transmit, ReceiveFrameDelegate receive)
    {
        if (hostname == null || hostnameLen <= 0)
            return 0;

        uint dnsServer = _stack.Config.DnsServer;
        if (dnsServer == 0)
        {
            Debug.WriteLine("[DNS] No DNS server configured");
            return 0;
        }

        // Allocate query buffer on stack
        byte* queryBuffer = stackalloc byte[DNS.MaxMessageSize];

        // Generate transaction ID
        ushort transactionId = _nextTransactionId++;

        // Build DNS query
        int queryLen = DNS.BuildQuery(queryBuffer, transactionId, hostname, hostnameLen);
        if (queryLen <= 0)
        {
            Debug.WriteLine("[DNS] Failed to build query");
            return 0;
        }

        Debug.Write("[DNS] Resolving hostname via ");
        PrintIP(dnsServer);
        Debug.WriteLine();

        // Send query via UDP. On a cold ARP cache the first SendUdp
        // returns 0: the stack queues an ARP request for the next hop
        // and drops the datagram (BuildIPv4Frame). Transmit whatever the
        // stack queued (datagram or ARP request) and retry the send on a
        // 500 ms cadence until the query is out, then keep resending
        // while waiting (the emulated NIC needs wall-clock time, and a
        // lost datagram must not fail the lookup).
        ushort localPort = 53000;  // Use a high port for our queries
        ulong startTime = Timer.GetUptimeMilliseconds();
        ulong lastSend = 0;
        bool sentOnce = false;
        byte* rxBuffer = stackalloc byte[1514];
        byte* responseBuffer = stackalloc byte[DNS.MaxMessageSize];

        while (true)
        {
            ulong elapsed = Timer.GetUptimeMilliseconds() - startTime;
            if (elapsed >= (ulong)timeoutMs)
            {
                Debug.WriteLine("[DNS] Timeout waiting for response");
                return 0;
            }

            // Resend on a 500 ms cadence until the query is out, then
            // slow to 2 s: a healthy lookup should not leave a pile of
            // duplicate replies in the kernel UDP queue (they used to
            // poison later lookups - see the drain loop below).
            ulong interval = sentOnce ? 2000UL : 500UL;
            if (lastSend == 0 || elapsed - lastSend >= interval)
            {
                lastSend = elapsed;
                int sent = _stack.SendUdp(dnsServer, localPort, DNS.Port, queryBuffer, queryLen);
                int pendingLen = _stack.GetPendingTxLen();
                if (pendingLen > 0)
                {
                    transmit(_stack.GetTxBuffer(), pendingLen);
                    if (sent > 0)
                        sentOnce = true;
                }
            }

            // Receive and process frames
            int rxLen = receive(rxBuffer, 1514);
            if (rxLen > 0)
                _stack.ProcessFrame(rxBuffer, rxLen);

            // Drain every queued reply for our flow. A previous lookup
            // that resent (slow reply) can leave duplicate replies with
            // an OLD transaction id at the queue head. The old code
            // consumed ONE entry, failed the id check and returned
            // failure outright - and once the 16-slot queue filled with
            // duplicates, fresh replies were dropped at enqueue,
            // wedging resolution until a DHCP cycle flushed the queue
            // (observed live: one resolve poisoned every later lookup).
            // Consume and discard stale duplicates and keep waiting for
            // the current transaction. The match (source IP + ports)
            // happens INSIDE ReceiveUdpFrom - comparing out-parameters
            // across that call boundary is the Tier-0 JIT hazard
            // documented on that method.
            while (_stack.UdpAvailable() > 0)
            {
                int recvLen = _stack.ReceiveUdpFrom(dnsServer, DNS.Port, localPort,
                                                    responseBuffer, DNS.MaxMessageSize);
                if (recvLen <= 0)
                    break;  // nothing matching left in the queue

                uint resolvedIP;
                if (DNS.ParseResponse(responseBuffer, recvLen, transactionId, out resolvedIP))
                {
                    Debug.Write("[DNS] Resolved to ");
                    PrintIP(resolvedIP);
                    Debug.WriteLine();
                    return resolvedIP;
                }
                // Stale duplicate from an earlier attempt - drop it.
            }
        }
    }

    /// <summary>
    /// Resolve a hostname string to an IPv4 address.
    /// </summary>
    /// <param name="hostname">Hostname string.</param>
    /// <param name="timeoutMs">Timeout in milliseconds.</param>
    /// <param name="transmit">Delegate to transmit frames.</param>
    /// <param name="receive">Delegate to receive frames.</param>
    /// <returns>IPv4 address in host byte order, or 0 on failure.</returns>
    public uint Resolve(string hostname, int timeoutMs,
                        TransmitFrameDelegate transmit, ReceiveFrameDelegate receive)
    {
        if (string.IsNullOrEmpty(hostname))
            return 0;

        // Check if hostname is already an IP address
        uint ip = TryParseIPAddress(hostname);
        if (ip != 0)
            return ip;

        // Convert string to byte array on stack
        int len = hostname.Length;
        byte* hostnameBytes = stackalloc byte[len];
        for (int i = 0; i < len; i++)
        {
            char c = hostname[i];
            if (c > 127)
            {
                Debug.WriteLine("[DNS] Non-ASCII hostname not supported");
                return 0;
            }
            hostnameBytes[i] = (byte)c;
        }

        return Resolve(hostnameBytes, len, timeoutMs, transmit, receive);
    }

    /// <summary>
    /// Resolve a hostname to an IPv6 address (AAAA record). Uses the
    /// IPv6 DNS server when one was learned (RDNSS/DHCPv6); otherwise
    /// queries the IPv4 server for the AAAA record (RFC 3596 allows
    /// transport-independent record types).
    /// </summary>
    /// <returns>Resolved address, or :: on failure.</returns>
    public Ipv6Address ResolveV6(string hostname, int timeoutMs,
                                 TransmitFrameDelegate transmit, ReceiveFrameDelegate receive)
    {
        Ipv6Address none = default;
        if (string.IsNullOrEmpty(hostname))
            return none;

        // Literal address?
        if (Ipv6Address.TryParse(hostname, out Ipv6Address literal))
            return literal;

        Ipv6Address dns6 = _stack.V6Dns;
        bool useV6 = !dns6.IsUnspecified;
        if (!useV6 && _stack.Config.DnsServer == 0)
        {
            Debug.WriteLine("[DNS] No DNS server configured");
            return none;
        }

        // Encode the hostname.
        int hlen = hostname.Length;
        byte* hostnameBytes = stackalloc byte[hlen];
        for (int i = 0; i < hlen; i++)
        {
            char c = hostname[i];
            if (c > 127)
                return none;
            hostnameBytes[i] = (byte)c;
        }

        byte* queryBuffer = stackalloc byte[DNS.MaxMessageSize];
        ushort transactionId = _nextTransactionId++;
        int queryLen = DNS.BuildQueryType(queryBuffer, transactionId, hostnameBytes, hlen,
            DNS.TypeAAAA);
        if (queryLen <= 0)
            return none;

        ushort localPort = 53001;
        if (useV6)
        {
            Debug.Write("[DNS] AAAA query via [");
            Debug.Write(dns6.ToString());
            Debug.WriteLine("]");
            int sent = _stack.SendUdp6(&dns6, localPort, DNS.Port, queryBuffer, queryLen);
            if (sent == 0)
            {
                Debug.WriteLine("[DNS] Failed to send v6 query (NDP needed?)");
                return none;
            }
        }
        else
        {
            int sent = _stack.SendUdp(_stack.Config.DnsServer, localPort, DNS.Port,
                queryBuffer, queryLen);
            if (sent == 0)
            {
                Debug.WriteLine("[DNS] Failed to send query (ARP needed?)");
                return none;
            }
        }

        int pendingLen = _stack.GetPendingTxLen();
        if (pendingLen > 0)
            transmit(_stack.GetTxBuffer(), pendingLen);

        ulong startTime = Timer.GetUptimeMilliseconds();
        byte* rxBuffer = stackalloc byte[1514];
        byte* responseBuffer = stackalloc byte[DNS.MaxMessageSize];

        while (true)
        {
            ulong elapsed = Timer.GetUptimeMilliseconds() - startTime;
            if (elapsed >= (ulong)timeoutMs)
            {
                Debug.WriteLine("[DNS] Timeout waiting for AAAA response");
                return none;
            }

            int rxLen = receive(rxBuffer, 1514);
            if (rxLen > 0)
            {
                _stack.ProcessFrame(rxBuffer, rxLen);

                if (useV6)
                {
                    while (_stack.Udp6Available() > 0)
                    {
                        Ipv6Address srcAddr;
                        ushort replyPort;
                        int recvLen = _stack.ReceiveUdp6To(localPort,
                            out srcAddr, out replyPort, responseBuffer, DNS.MaxMessageSize);
                        if (recvLen <= 0)
                            break;

                        Ipv6Address resolved;
                        if (DNS.ParseResponseAAAA(responseBuffer, recvLen, transactionId,
                            out resolved))
                            return resolved;
                        // Stale duplicate from an earlier attempt - drop it.
                    }
                }
                else
                {
                    // Drain every queued reply for our flow: stale
                    // duplicates must not fail the lookup (see the
                    // same loop in Resolve() for the full story).
                    while (_stack.UdpAvailable() > 0)
                    {
                        // Match inside the stack method (see ReceiveUdpFrom).
                        int recvLen = _stack.ReceiveUdpFrom(_stack.Config.DnsServer, DNS.Port, localPort,
                            responseBuffer, DNS.MaxMessageSize);
                        if (recvLen <= 0)
                            break;

                        Ipv6Address resolved;
                        if (DNS.ParseResponseAAAA(responseBuffer, recvLen, transactionId,
                            out resolved))
                            return resolved;
                        // Stale duplicate from an earlier attempt - drop it.
                    }
                }
            }
        }
    }

    /// <summary>
    /// Try to parse a string as an IP address (e.g., "192.168.1.1").
    /// </summary>
    /// <param name="s">String to parse.</param>
    /// <returns>IP address in host byte order, or 0 if not a valid IP.</returns>
    private static uint TryParseIPAddress(string s)
    {
        if (string.IsNullOrEmpty(s))
            return 0;

        int[] octets = new int[4];
        int octetIndex = 0;
        int currentValue = 0;
        bool hasDigit = false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (c >= '0' && c <= '9')
            {
                currentValue = currentValue * 10 + (c - '0');
                if (currentValue > 255)
                    return 0;
                hasDigit = true;
            }
            else if (c == '.')
            {
                if (!hasDigit || octetIndex >= 3)
                    return 0;
                octets[octetIndex++] = currentValue;
                currentValue = 0;
                hasDigit = false;
            }
            else
            {
                // Non-digit, non-dot character - not an IP
                return 0;
            }
        }

        // Final octet
        if (!hasDigit || octetIndex != 3)
            return 0;
        octets[3] = currentValue;

        // Convert to uint (host byte order: MSB first)
        return ((uint)octets[0] << 24) |
               ((uint)octets[1] << 16) |
               ((uint)octets[2] << 8) |
               (uint)octets[3];
    }

    /// <summary>
    /// Print an IP address for debugging.
    /// </summary>
    private static void PrintIP(uint ip)
    {
        Debug.WriteDecimal((ip >> 24) & 0xFF);
        Debug.Write(".");
        Debug.WriteDecimal((ip >> 16) & 0xFF);
        Debug.Write(".");
        Debug.WriteDecimal((ip >> 8) & 0xFF);
        Debug.Write(".");
        Debug.WriteDecimal(ip & 0xFF);
    }
}
