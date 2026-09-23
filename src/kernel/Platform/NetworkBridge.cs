// NeutrinoOS kernel - network bridge for the shell's network utilities
//
// The virtio-net driver is a JIT-loaded assembly; utilities run in the
// same JIT world but cannot call driver code directly. This bridge
// captures the driver's frame entry points (TransmitFrame / ReceiveFrame)
// at driver-bind time - the same technique FileExports uses for the AHCI
// driver - and exposes them to the kernel through Kernel-callable
// wrappers (registered as Kernel_Net* exports, see Exports/DDK).
//
// The bridge also invokes VirtioNetEntry.GetNetworkStack() once so the
// DDK's NetworkManager/NetworkStack state (shared static state across
// the JIT world) contains the eth0 interface for the utilities.

using System;

namespace ProtonOS.Platform;

/// <summary>Virtio-net frame pump bridge (see file header).</summary>
public static unsafe class NetworkBridge
{
    private static void* _fnTransmit;
    private static void* _fnReceive;
    private static bool _registerAttempted;

    /// <summary>True when the driver frame pump has been captured.</summary>
    public static bool IsReady => _fnTransmit != null && _fnReceive != null;

    /// <summary>
    /// Capture the virtio-net driver's frame entry points and create the
    /// DDK network stack/interface. Called once during driver binding;
    /// idempotent.
    /// </summary>
    public static void Register(uint asmId, uint typeToken)
    {
        if (_registerAttempted)
            return;
        _registerAttempted = true;

        _fnTransmit = CaptureMethod(asmId, typeToken, "TransmitFrame");
        _fnReceive = CaptureMethod(asmId, typeToken, "ReceiveFrame");

        // Materialize the network stack and NetworkManager interface
        // (side-effect only; the objects are owned by the DDK statics).
        void* getStack = CaptureMethod(asmId, typeToken, "GetNetworkStack");
        if (getStack != null)
        {
            var fn = (delegate* unmanaged<void*>)getStack;
            fn();
        }

        DebugConsole.Write("[NetBridge] driver pump ");
        if (IsReady)
            DebugConsole.WriteLine("ready (transmit + receive captured)");
        else
            DebugConsole.WriteLine("NOT captured (receive/transmit missing)");
    }

    private static void* CaptureMethod(uint asmId, uint typeToken, string name)
    {
        uint token = Runtime.AssemblyLoader.FindMethodDefByName(asmId, typeToken, name);
        if (token == 0)
            return null;

        var result = Runtime.JIT.Tier0JIT.CompileMethod(asmId, token);
        if (!result.Success || result.CodeAddress == null)
            return null;
        return result.CodeAddress;
    }

    /// <summary>Send a frame through the NIC; returns 1 on success.</summary>
    public static int Transmit(byte* data, int length)
    {
        if (_fnTransmit == null)
            return 0;
        var fn = (delegate* unmanaged<byte*, int, bool>)_fnTransmit;
        return fn(data, length) ? 1 : 0;
    }

    /// <summary>Receive a frame from the NIC; returns length or 0.</summary>
    public static int Receive(byte* buffer, int maxLength)
    {
        if (_fnReceive == null)
            return 0;
        var fn = (delegate* unmanaged<byte*, int, int>)_fnReceive;
        return fn(buffer, maxLength);
    }
}
