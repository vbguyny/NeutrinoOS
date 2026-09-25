// ProtonOS Kernel - AOT bridges for kernel driver services (Phase 8).
//
// Packaged drivers are JIT-compiled: their calls to IDriverServices land on
// the kernel's KernelDriverServices object, which is AOT-compiled and has
// no JIT-visible metadata - neither its MethodTable carries the driver's
// copy of the interface nor do its methods appear in any assembly the JIT
// can search (verified: the cross-world slot lookup misses and the by-name
// hierarchy search finds nothing, leaving the dispatch target at 0).
//
// These bridges close the gap: each IDriverServices method gets a tiny
// static AOT forwarder (receiver first, matching the JIT's emitted
// instance-call register shape) registered in the AOT method registry
// keyed by the INTERFACE full name. JitStubs.ResolveInterfaceMethodByName
// falls back to that registry when metadata resolution fails, so the
// dispatch returns the bridge's native address and the call lands in the
// kernel AOT world. Same pattern as the StringHelpers/ThreadHelpers
// bridges; argument counts here exclude the receiver.

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Runtime;
using ProtonOS.Runtime.JIT;

namespace ProtonOS.Drivers;

/// <summary>AOT bridges exposing KernelDriverServices to JIT-compiled drivers.</summary>
internal static unsafe class DriverServicesBridges
{
    private const string IfaceName = "NeutrinoOS.Drivers.IDriverServices";

    private static ulong MapMmioBridge(KernelDriverServices s, ulong physicalAddress, ulong size)
    {
        return s.MapMmio(physicalAddress, size);
    }

    private static void UnmapMmioBridge(KernelDriverServices s, ulong address, ulong size)
    {
        s.UnmapMmio(address, size);
    }

    private static ulong AllocateDmaBridge(KernelDriverServices s, ulong size, ulong alignment)
    {
        return s.AllocateDma(size, alignment);
    }

    private static void FreeDmaBridge(KernelDriverServices s, ulong address, ulong size)
    {
        s.FreeDma(address, size);
    }

    private static bool RegisterInterruptBridge(KernelDriverServices s, int irq, DriverInterruptCallback handler)
    {
        return s.RegisterInterrupt(irq, handler);
    }

    private static void UnregisterInterruptBridge(KernelDriverServices s, int irq)
    {
        s.UnregisterInterrupt(irq);
    }

    private static string CreateDeviceNodeBridge(KernelDriverServices s, string name, int major, int minor)
    {
        return s.CreateDeviceNode(name, major, minor);
    }

    private static bool RemoveDeviceNodeBridge(KernelDriverServices s, string name)
    {
        return s.RemoveDeviceNode(name);
    }

    private static void LogBridge(KernelDriverServices s, DriverLogLevel level, string message)
    {
        s.Log(level, message);
    }

    /// <summary>
    /// Register every bridge with the AOT method registry (called once at
    /// driver-framework init, before packaged drivers are loaded).
    /// </summary>
    public static void Register()
    {
        AotMethodRegistry.Register(IfaceName, "MapMmio",
            (nint)(delegate*<KernelDriverServices, ulong, ulong, ulong>)&MapMmioBridge,
            2, ReturnKind.Int64, true, false);
        AotMethodRegistry.Register(IfaceName, "UnmapMmio",
            (nint)(delegate*<KernelDriverServices, ulong, ulong, void>)&UnmapMmioBridge,
            2, ReturnKind.Void, true, false);
        AotMethodRegistry.Register(IfaceName, "AllocateDma",
            (nint)(delegate*<KernelDriverServices, ulong, ulong, ulong>)&AllocateDmaBridge,
            2, ReturnKind.Int64, true, false);
        AotMethodRegistry.Register(IfaceName, "FreeDma",
            (nint)(delegate*<KernelDriverServices, ulong, ulong, void>)&FreeDmaBridge,
            2, ReturnKind.Void, true, false);
        AotMethodRegistry.Register(IfaceName, "RegisterInterrupt",
            (nint)(delegate*<KernelDriverServices, int, DriverInterruptCallback, bool>)&RegisterInterruptBridge,
            2, ReturnKind.Int32, true, false);
        AotMethodRegistry.Register(IfaceName, "UnregisterInterrupt",
            (nint)(delegate*<KernelDriverServices, int, void>)&UnregisterInterruptBridge,
            1, ReturnKind.Void, true, false);
        AotMethodRegistry.Register(IfaceName, "CreateDeviceNode",
            (nint)(delegate*<KernelDriverServices, string, int, int, string>)&CreateDeviceNodeBridge,
            3, ReturnKind.IntPtr, true, false);
        AotMethodRegistry.Register(IfaceName, "RemoveDeviceNode",
            (nint)(delegate*<KernelDriverServices, string, bool>)&RemoveDeviceNodeBridge,
            1, ReturnKind.Int32, true, false);
        AotMethodRegistry.Register(IfaceName, "Log",
            (nint)(delegate*<KernelDriverServices, DriverLogLevel, string, void>)&LogBridge,
            2, ReturnKind.Void, true, false);
    }
}
