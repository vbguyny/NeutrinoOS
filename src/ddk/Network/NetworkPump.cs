// ProtonOS DDK - Network frame pump (Phase 5)
//
// The kernel captures the virtio-net driver's frame entry points at
// bind time and exposes them as Kernel_Net* exports (see the kernel's
// Platform.NetworkBridge). Utilities use this class to pump Ethernet
// frames between the NIC and a NetworkStack instance, which is how the
// DDK stack model works: the stack builds frames into its TX buffer and
// parses received frames, while the caller moves the bytes.
//
// The DDK assembly (and therefore this pump, the NetworkManager state
// and the NetworkStack instances) is shared between the driver, the
// kernel tests and the utilities, so the eth0 interface registered by
// the driver is visible here.

using System;
using System.Runtime.InteropServices;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network.Stack;

namespace ProtonOS.DDK.Network;

/// <summary>NIC frame pump + high-level helpers (see file header).</summary>
public static unsafe class NetworkPump
{
    /// <summary>1 when a network device (frame pump) is available.</summary>
    [DllImport("*", EntryPoint = "Kernel_NetPresent")]
    public static extern int NetPresent();

    /// <summary>Send one Ethernet frame; returns 1 on success.</summary>
    [DllImport("*", EntryPoint = "Kernel_NetTransmit")]
    public static extern int NetTransmit(byte* data, int length);

    /// <summary>Receive one Ethernet frame; returns length or 0.</summary>
    [DllImport("*", EntryPoint = "Kernel_NetReceive")]
    public static extern int NetReceive(byte* buffer, int maxLength);

    /// <summary>True when a network device is bound.</summary>
    public static bool IsAvailable => NetPresent() != 0;

    /// <summary>Send a frame through the NIC.</summary>
    public static bool TransmitFrame(byte* data, int length) => NetTransmit(data, length) > 0;

    /// <summary>Receive a frame from the NIC (0 = none pending).</summary>
    public static int ReceiveFrame(byte* buffer, int maxLength) => NetReceive(buffer, maxLength);

    /// <summary>Delegate adapter: DhcpClient/DnsResolver transmit.</summary>
    public static void TransmitAdapter(byte* data, int length) => TransmitFrame(data, length);

    /// <summary>Delegate adapter: DhcpClient/DnsResolver receive.</summary>
    public static int ReceiveAdapter(byte* buffer, int maxLength) => ReceiveFrame(buffer, maxLength);

    /// <summary>Transmit the frame the stack just built into its TX buffer.</summary>
    public static bool TransmitTxBuffer(NetworkStack stack, int frameLength)
    {
        // Phase 7: kernel loopback - frames addressed to a local address
        // (the interface IP or 127.0.0.0/8) are delivered straight back
        // into the stack instead of going to the NIC.
        byte* frame = stack.GetTxBuffer();
        if (frameLength >= 34 && frame[12] == 0x08 && frame[13] == 0x00)
        {
            uint destIP = ((uint)frame[30] << 24) | ((uint)frame[31] << 16) |
                          ((uint)frame[32] << 8) | frame[33];
            if (stack.IsLocalAddress(destIP))
            {
                stack.ProcessFrame(frame, frameLength);
                return true;
            }
        }
        // IPv6 loopback (Phase 9): ::1 and any assigned address.
        if (frameLength >= 54 && frame[12] == 0x86 && frame[13] == 0xDD)
        {
            Ipv6Address dest = Ipv6Address.FromBytes(frame + 14 + 24);
            if (stack.IsLocalV6(&dest))
            {
                stack.ProcessFrame(frame, frameLength);
                return true;
            }
        }
        return TransmitFrame(frame, frameLength);
    }

    /// <summary>
    /// Flush any frame the stack queued for transmission (e.g. the TCP
    /// SYN built by TcpConnect, ACKs/segments queued while processing a
    /// received frame, or data queued by TcpSend). The stack model
    /// requires the caller to move frames; nothing goes out otherwise.
    /// </summary>
    public static int FlushTx(NetworkStack stack)
    {
        int transmitted = 0;
        for (int i = 0; i < 16; i++)
        {
            int len = stack.GetPendingTxLen();
            if (len <= 0)
                break;
            TransmitTxBuffer(stack, len);
            transmitted++;
        }
        return transmitted;
    }

    /// <summary>
    /// Move up to <paramref name="maxFrames"/> pending NIC frames into
    /// the stack, transmitting any response frames the stack queues.
    /// Returns the number of frames processed.
    /// </summary>
    public static int Pump(NetworkStack stack, int maxFrames)
    {
        byte* buffer = stackalloc byte[1600];
        int processed = 0;
        for (int i = 0; i < maxFrames; i++)
        {
            int len = ReceiveFrame(buffer, 1600);
            if (len <= 0)
                break;
            stack.ProcessFrame(buffer, len);
            FlushTx(stack);
            processed++;
        }
        stack.ReapClosedConnections();
        return processed;
    }

    /// <summary>
    /// Resolve an IP to a MAC through ARP: sends a request when needed
    /// and pumps the stack for up to <paramref name="maxMs"/> ms (real
    /// time - instant poll loops are far too fast for the emulated NIC,
    /// so the request is re-sent every 500 ms while waiting).
    /// </summary>
    public static bool ResolveArp(NetworkStack stack, uint ip, int maxMs)
    {
        // ARP resolves link-layer next hops, not final destinations: an
        // off-subnet address resolves through the gateway. The cache is
        // keyed by next hop - BuildIPv4Frame looks the gateway up when it
        // builds a frame for a remote destination, so resolving the raw
        // destination here would never make the frame sendable. (QEMU's
        // user-mode network answers ARP for any address, which masked
        // this; VirtualBox NAT only answers for its own addresses.)
        uint nextHop = stack.Config.IsLocalSubnet(ip) ? ip : stack.Config.Gateway;
        if (nextHop == 0)
            return false;

        byte* mac = stackalloc byte[6];
        if (stack.ArpCache.Lookup(nextHop, mac))
            return true;

        ulong start = Timer.GetUptimeMilliseconds();
        ulong lastProbe = 0;
        while (Timer.GetUptimeMilliseconds() - start < (ulong)maxMs)
        {
            ulong now = Timer.GetUptimeMilliseconds();
            if (now - start - lastProbe >= 500 || lastProbe == 0)
            {
                lastProbe = now - start;
                int len = stack.SendArpRequest(nextHop);
                if (len > 0)
                    TransmitTxBuffer(stack, len);
            }
            Pump(stack, 4);
            if (stack.ArpCache.Lookup(nextHop, mac))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve an IPv6 next-hop to a MAC through neighbor discovery.
    /// Runs for up to <paramref name="maxMs"/> milliseconds (real time),
    /// resending the solicitation periodically (mirrors ResolveArp, but
    /// with a millisecond budget: the emulated network needs wall-clock
    /// time to answer, a fast poll loop is not enough).
    /// </summary>
    public static bool ResolveNdp(NetworkStack stack, Ipv6Address* target, int maxMs)
    {
        byte* mac = stackalloc byte[6];
        if (stack.LookupNdp(target, mac))
            return true;

        ulong start = Timer.GetUptimeMilliseconds();
        ulong lastProbe = 0;
        while (Timer.GetUptimeMilliseconds() - start < (ulong)maxMs)
        {
            ulong now = Timer.GetUptimeMilliseconds();
            if (now - start - lastProbe >= 500 || lastProbe == 0)
            {
                lastProbe = now - start;
                int len = stack.SendNeighborSolicitation(target);
                if (len > 0)
                    TransmitTxBuffer(stack, len);
            }
            Pump(stack, 4);
            if (stack.LookupNdp(target, mac))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Bring the IPv6 side up: link-local address + Router Solicitation,
    /// pumping (in real time, up to <paramref name="maxMs"/>) until a
    /// router advertisement arrives. Returns true when a router was
    /// heard (SLAAC address adopted).
    /// </summary>
    public static bool BringUpV6(NetworkStack stack, int maxMs)
    {
        stack.ConfigureV6LinkLocal();
        ulong start = Timer.GetUptimeMilliseconds();
        ulong lastProbe = 0;
        while (Timer.GetUptimeMilliseconds() - start < (ulong)maxMs)
        {
            ulong now = Timer.GetUptimeMilliseconds();
            if (now - start - lastProbe >= 1000 || lastProbe == 0)
            {
                lastProbe = now - start;
                int len = stack.SendRouterSolicitation();
                if (len > 0)
                    TransmitTxBuffer(stack, len);
            }
            Pump(stack, 4);
            if (stack.V6RouterSeen)
                return true;
        }
        return false;
    }
}
