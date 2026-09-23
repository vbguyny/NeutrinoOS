// NeutrinoOS kernel - DDK network exports (Phase 5)
//
// Exposes the virtio-net frame pump (captured by Platform.NetworkBridge
// when the driver binds) to utilities through the Kernel_Net* export
// ABI. The exports always exist; they report unavailable (0) when no
// network device was bound.

using System.Runtime.InteropServices;

namespace ProtonOS.Exports.DDK;

/// <summary>Network frame pump exports (see file header).</summary>
public static unsafe class NetworkExports
{
    /// <summary>1 when a network device (frame pump) is available.</summary>
    [UnmanagedCallersOnly]
    public static int NetPresent()
    {
        return Platform.NetworkBridge.IsReady ? 1 : 0;
    }

    /// <summary>Send one Ethernet frame; returns 1 on success.</summary>
    [UnmanagedCallersOnly]
    public static int NetTransmit(byte* data, int length)
    {
        return Platform.NetworkBridge.Transmit(data, length);
    }

    /// <summary>Receive one Ethernet frame; returns length or 0.</summary>
    [UnmanagedCallersOnly]
    public static int NetReceive(byte* buffer, int maxLength)
    {
        return Platform.NetworkBridge.Receive(buffer, maxLength);
    }
}
