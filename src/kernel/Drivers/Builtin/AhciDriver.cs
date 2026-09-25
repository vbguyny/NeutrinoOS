// ProtonOS Kernel - built-in AHCI driver (SATA AHCI controllers).
//
// Binds the SATA AHCI PCI function in the device tree (class 01/06). The
// controller, ports and filesystem engine keep running through the legacy
// AHCI path (the JIT'd ProtonOS.Drivers.Ahci assembly mounted the root and
// boot volumes before the framework started); this driver gives the
// framework ownership of the controller node and logs the handoff. Taking
// the ABAR behind the framework is deliberately deferred to the full port.

using System;
using NeutrinoOS.Drivers;

namespace ProtonOS.Drivers.Builtin;

/// <summary>AHCI (SATA) storage controller driver.</summary>
public sealed class AhciDriver : IDriver
{
    private IDriverServices _services;

    public string Name => "ahci";

    public string Version => "1.0.0";

    public int AbiMajor => DriverAbi.Major;

    public int AbiMinor => DriverAbi.Minor;

    /// <summary>Stores the kernel services instance injected by the manager.</summary>
    public void Initialize(IDriverServices services)
    {
        _services = services;
    }

    /// <summary>Matches PCI SATA AHCI controllers (base class 01, subclass 06).</summary>
    public bool Match(DeviceInfo device)
    {
        return device.Bus == "pci" && (device.ClassCode >> 8) == 0x0106;
    }

    /// <summary>The controller is already initialized by the legacy storage path.</summary>
    public bool Probe(DeviceInfo device)
    {
        _ = device;
        return true;
    }

    /// <summary>Logs the framework handoff (no register access yet).</summary>
    public bool Start(DeviceInfo device)
    {
        if (_services == null)
            return false;
        _services.Log(DriverLogLevel.Info, "ahci started on " + device.Path
            + " (controller owned; boot volume still on the legacy AHCI path)");
        return true;
    }

    /// <summary>Nothing to release yet (no registers claimed).</summary>
    public void Stop(DeviceInfo device)
    {
        _ = device;
    }
}
