// ProtonOS Kernel - driver ABI access bridges (Phase 8, driver framework).
//
// JIT-compiled driver code reads device properties (device.Bus,
// device.Path, device.DeviceId, ...) through MemberRefs against
// NeutrinoOS.Driver.Abstractions types. Those types resolve onto the
// kernel's compiled-in copies (AssemblyLoader.ResolveAssemblyRef), and
// their methods become reachable from JIT code only through
// AotMethodRegistry entries: the JIT resolves such a MemberRef by
// (type name, method name, arg count) and needs an AOT code address.
//
// These tiny static bridges provide it. Each bridge takes the receiver as
// its first parameter - exactly the call shape the JIT emits for an
// instance getter (HasThis=true, "this" in RCX) - mirroring the
// StringHelpers / ThreadHelpers pattern in AotMethodRegistry.

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Runtime;
using ProtonOS.Runtime.JIT;

namespace ProtonOS.Drivers;

/// <summary>AOT bridges for the DeviceInfo surface (receiver first).</summary>
internal static unsafe class DriverAbiBridges
{
    public static int GetId(DeviceInfo d) => d.Id;
    public static int GetParentId(DeviceInfo d) => d.ParentId;
    public static string GetPath(DeviceInfo d) => d.Path;
    public static ushort GetVendorId(DeviceInfo d) => d.VendorId;
    public static ushort GetDeviceId(DeviceInfo d) => d.DeviceId;
    public static int GetClass(DeviceInfo d) => (int)d.Class;
    public static uint GetClassCode(DeviceInfo d) => d.ClassCode;
    public static string GetBus(DeviceInfo d) => d.Bus;
    public static uint GetAddress(DeviceInfo d) => d.Address;
    public static DeviceResource[] GetResources(DeviceInfo d) => d.Resources;
    public static int GetStatus(DeviceInfo d) => (int)d.Status;

    /// <summary>
    /// Register every bridge with the AOT method registry (called once at
    /// driver-framework init). ushort/enum/uint returns register as Int32
    /// (zero-extended in RAX); strings and arrays as IntPtr.
    /// </summary>
    public static void Register()
    {
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_Id",
            (nint)(delegate*<DeviceInfo, int>)&GetId, 0, ReturnKind.Int32, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_ParentId",
            (nint)(delegate*<DeviceInfo, int>)&GetParentId, 0, ReturnKind.Int32, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_Path",
            (nint)(delegate*<DeviceInfo, string>)&GetPath, 0, ReturnKind.IntPtr, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_VendorId",
            (nint)(delegate*<DeviceInfo, ushort>)&GetVendorId, 0, ReturnKind.Int32, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_DeviceId",
            (nint)(delegate*<DeviceInfo, ushort>)&GetDeviceId, 0, ReturnKind.Int32, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_Class",
            (nint)(delegate*<DeviceInfo, int>)&GetClass, 0, ReturnKind.Int32, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_ClassCode",
            (nint)(delegate*<DeviceInfo, uint>)&GetClassCode, 0, ReturnKind.Int32, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_Bus",
            (nint)(delegate*<DeviceInfo, string>)&GetBus, 0, ReturnKind.IntPtr, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_Address",
            (nint)(delegate*<DeviceInfo, uint>)&GetAddress, 0, ReturnKind.Int32, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_Resources",
            (nint)(delegate*<DeviceInfo, DeviceResource[]>)&GetResources, 0, ReturnKind.IntPtr, true, false);
        AotMethodRegistry.Register("NeutrinoOS.Drivers.DeviceInfo", "get_Status",
            (nint)(delegate*<DeviceInfo, int>)&GetStatus, 0, ReturnKind.Int32, true, false);
    }
}
