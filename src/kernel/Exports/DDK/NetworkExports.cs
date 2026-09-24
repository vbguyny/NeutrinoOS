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
    // Phase 7 interface-level counters (see /dev/netstats).
    private static ulong _framesIn;
    private static ulong _framesOut;
    private static ulong _bytesIn;
    private static ulong _bytesOut;
    private static ulong _txErrors;

    /// <summary>Interface-level frame/byte counters (Phase 7).</summary>
    public static void GetFrameStats(out ulong framesIn, out ulong framesOut,
                                     out ulong bytesIn, out ulong bytesOut, out ulong txErrors)
    {
        framesIn = _framesIn;
        framesOut = _framesOut;
        bytesIn = _bytesIn;
        bytesOut = _bytesOut;
        txErrors = _txErrors;
    }

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
        int rc = Platform.NetworkBridge.Transmit(data, length);
        if (rc > 0)
        {
            _framesOut++;
            _bytesOut += (ulong)length;
        }
        else
        {
            _txErrors++;
        }
        return rc;
    }

    /// <summary>Receive one Ethernet frame; returns length or 0.</summary>
    [UnmanagedCallersOnly]
    public static int NetReceive(byte* buffer, int maxLength)
    {
        int len = Platform.NetworkBridge.Receive(buffer, maxLength);
        if (len > 0)
        {
            _framesIn++;
            _bytesIn += (ulong)len;
        }
        return len;
    }
}
