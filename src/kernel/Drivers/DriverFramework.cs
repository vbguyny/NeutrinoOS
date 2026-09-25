// ProtonOS Kernel - driver framework boot initialization.
//
// Called from Kernel.Main after the PCI bus has been scanned. Builds the
// device tree (PCI + VirtIO + platform devices), initializes the driver
// manager and runs the first match pass. Driver bindings happen here; the
// ported drivers register themselves via DriverManager.Register before
// InitializeMatch runs.

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Platform;

namespace ProtonOS.Drivers;

/// <summary>Boot-time driver framework setup.</summary>
public static class DriverFramework
{
    private static bool _initialized;

    /// <summary>True once the framework has been initialized this boot.</summary>
    public static bool Initialized => _initialized;

    /// <summary>
    /// Initialize the driver framework: enumerate buses into the device
    /// tree, register built-in drivers and run the first match pass.
    /// Idempotent.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        DebugConsole.Write("[drv] framework init, ABI ");
        DebugConsole.WriteLine(DriverAbi.VersionString);

        KernelDeviceTree tree = KernelDeviceTree.Instance;

        int pciCount = PciBusEnumerator.Enumerate();
        int virtioCount = VirtioBusEnumerator.Enumerate();
        AddPlatformDevices(tree);

        LogTree(tree, pciCount, virtioCount);

        DriverManager.Initialize(tree);
        RegisterBuiltins();
        int started = DriverManager.MatchAll();

        DebugConsole.Write("[drv] ");
        DebugConsole.WriteDecimal((uint)DriverManager.DriverCount);
        DebugConsole.Write(" driver(s) registered, ");
        DebugConsole.WriteDecimal((uint)started);
        DebugConsole.WriteLine(" device(s) started");
    }

    /// <summary>
    /// Register kernel built-in drivers. The ported drivers (UART, PS/2,
    /// VGA, VirtIO family) hook in here as they are moved onto the
    /// framework; until then the legacy kernel paths keep them working.
    /// </summary>
    private static void RegisterBuiltins()
    {
        DriverManager.Register(new Builtin.Uart16550Driver());
        DriverManager.Register(new Builtin.Ps2KeyboardDriver());
        DriverManager.Register(new Builtin.VgaTextConsoleDriver());
    }

    /// <summary>Legacy platform devices that are not on a discoverable bus.</summary>
    private static void AddPlatformDevices(KernelDeviceTree tree)
    {
        // COM1 serial port (16550): I/O 0x3F8..0x3FF, IRQ4.
        DeviceResource[] uartResources = new DeviceResource[2];
        uartResources[0] = new DeviceResource(DeviceResourceKind.IoPort, 0x3F8, 8);
        uartResources[1] = new DeviceResource(DeviceResourceKind.Irq, 4, 0);
        tree.Add(0, "platform", 0x3F8, 0xFFFF, 0x1650, DeviceClass.Serial, 0, "platform/uart0", uartResources);

        // PS/2 controller (8042): I/O 0x60/0x64, IRQ1 (keyboard) and IRQ12 (mouse).
        DeviceResource[] ps2Resources = new DeviceResource[3];
        ps2Resources[0] = new DeviceResource(DeviceResourceKind.IoPort, 0x60, 5);
        ps2Resources[1] = new DeviceResource(DeviceResourceKind.Irq, 1, 0);
        ps2Resources[2] = new DeviceResource(DeviceResourceKind.Irq, 12, 0);
        tree.Add(0, "platform", 0x60, 0xFFFF, 0x8042, DeviceClass.Input, 0, "platform/ps2", ps2Resources);

        // Virtual test device (vid 0xFFFF, did 0x1601, no resources): gives
        // packaged drivers a deterministic match target in acceptance runs.
        // Nothing binds it unless an installed driver package matches it.
        DeviceResource[] vtestResources = new DeviceResource[0];
        tree.Add(0, "platform", 0x1601, 0xFFFF, 0x1601, DeviceClass.System, 0, "platform/vtest0", vtestResources);
    }

    /// <summary>
    /// Second framework phase (after the root filesystem is mounted): load
    /// driver packages installed by npkg and match them against the tree.
    /// </summary>
    public static int LoadPackagedDrivers()
    {
        int loaded = DriverPackageLoader.LoadAll();
        if (loaded <= 0)
            return 0;

        int started = DriverManager.MatchAll();
        DebugConsole.Write("[drv] ");
        DebugConsole.WriteDecimal((uint)loaded);
        DebugConsole.Write(" packaged driver(s) loaded, ");
        DebugConsole.WriteDecimal((uint)started);
        DebugConsole.WriteLine(" device(s) started");
        return loaded;
    }

    /// <summary>Log every device in the tree (boot diagnostics).</summary>
    private static void LogTree(KernelDeviceTree tree, int pciCount, int virtioCount)
    {
        for (int i = 0; i < tree.Count; i++)
        {
            DeviceInfo d = tree.GetAt(i);
            if (i == 0)
                continue;

            DebugConsole.Write("[DeviceTree] ");
            DebugConsole.Write(d.Path);
            DebugConsole.Write(" bus=");
            DebugConsole.Write(d.Bus);
            DebugConsole.Write(" vid:did=");
            DebugConsole.Write(Hex4(d.VendorId));
            DebugConsole.Write(":");
            DebugConsole.Write(Hex4(d.DeviceId));
            DebugConsole.Write(" class=");
            DebugConsole.WriteDecimal((uint)d.Class);
            DebugConsole.Write(" resources=");
            DebugConsole.WriteDecimal((uint)d.Resources.Length);
            DebugConsole.WriteLine();
        }

        DebugConsole.Write("[DeviceTree] ");
        DebugConsole.WriteDecimal((uint)tree.DeviceCount);
        DebugConsole.Write(" device(s): pci=");
        DebugConsole.WriteDecimal((uint)pciCount);
        DebugConsole.Write(" virtio=");
        DebugConsole.WriteDecimal((uint)virtioCount);
        DebugConsole.WriteLine();
    }

    private static char HexDigit(int v)
    {
        v &= 0xF;
        return v < 10 ? (char)('0' + v) : (char)('A' + (v - 10));
    }

    private static string Hex4(ushort v)
    {
        return new string(new char[]
        {
            HexDigit(v >> 12), HexDigit(v >> 8), HexDigit(v >> 4), HexDigit(v)
        });
    }
}
