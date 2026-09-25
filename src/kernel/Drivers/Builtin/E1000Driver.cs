// ProtonOS Kernel - built-in E1000 driver (Intel 8254x family).
//
// Binds the QEMU/VirtualBox e1000 PCI function in the device tree. Start
// maps BAR0 through IDriverServices (proving the MMIO path for a PCI
// network controller) while the packet/stack work keeps running through
// the legacy kernel network path until the full port moves I/O behind the
// framework.

using System;
using NeutrinoOS.Drivers;

namespace ProtonOS.Drivers.Builtin;

/// <summary>Intel 8254x (e1000) PCI network driver.</summary>
public sealed class E1000Driver : IDriver
{
    private IDriverServices _services;
    private ulong _mmio;
    private ulong _mmioLength;

    public string Name => "e1000";

    public string Version => "1.0.0";

    public int AbiMajor => DriverAbi.Major;

    public int AbiMinor => DriverAbi.Minor;

    /// <summary>Stores the kernel services instance injected by the manager.</summary>
    public void Initialize(IDriverServices services)
    {
        _services = services;
    }

    /// <summary>Matches the Intel PCI ids used by QEMU and VirtualBox e1000 NICs.</summary>
    public bool Match(DeviceInfo device)
    {
        if (device.Bus != "pci" || device.VendorId != 0x8086)
            return false;
        ushort did = device.DeviceId;
        return did == 0x10D3 || did == 0x100E || did == 0x100F || did == 0x15A3;
    }

    /// <summary>Requires the BAR0 register window (128 KiB on this family).</summary>
    public bool Probe(DeviceInfo device)
    {
        DeviceResource mmio;
        return device.TryGetResource(DeviceResourceKind.Mmio, out mmio) && mmio.Length >= 0x20000;
    }

    /// <summary>Maps BAR0 through the driver services ABI.</summary>
    public bool Start(DeviceInfo device)
    {
        if (_services == null)
            return false;

        DeviceResource mmio;
        if (!device.TryGetResource(DeviceResourceKind.Mmio, out mmio))
        {
            _services.Log(DriverLogLevel.Warning, "e1000: no MMIO resource");
            return false;
        }

        _mmio = _services.MapMmio(mmio.Base, mmio.Length);
        if (_mmio == 0)
        {
            _services.Log(DriverLogLevel.Error, "e1000: BAR0 mapping failed");
            return false;
        }
        _mmioLength = mmio.Length;
        _services.Log(DriverLogLevel.Info, "e1000 started (bar0 0x" + BuiltinHex.Hex(mmio.Base)
            + " len 0x" + BuiltinHex.Hex(mmio.Length)
            + "; legacy network path keeps the stack)");
        return true;
    }

    /// <summary>Releases the BAR mapping.</summary>
    public void Stop(DeviceInfo device)
    {
        _ = device;
        if (_services != null && _mmio != 0)
        {
            _services.UnmapMmio(_mmio, _mmioLength);
            _mmio = 0;
            _mmioLength = 0;
        }
    }
}
