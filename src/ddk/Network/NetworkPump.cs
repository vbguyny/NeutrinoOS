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
        => TransmitFrame(stack.GetTxBuffer(), frameLength);

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
        return processed;
    }

    /// <summary>
    /// Resolve an IP to a MAC through ARP: sends a request when needed
    /// and pumps the stack until the cache is populated or the poll
    /// budget runs out.
    /// </summary>
    public static bool ResolveArp(NetworkStack stack, uint ip, int maxPolls)
    {
        byte* mac = stackalloc byte[6];
        if (stack.ArpCache.Lookup(ip, mac))
            return true;

        int len = stack.SendArpRequest(ip);
        if (len > 0)
            TransmitTxBuffer(stack, len);

        for (int i = 0; i < maxPolls; i++)
        {
            Pump(stack, 4);
            if (stack.ArpCache.Lookup(ip, mac))
                return true;
        }
        return false;
    }
}
