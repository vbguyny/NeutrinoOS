// NeutrinoOS kernel - built-in xHCI USB host controller driver
// (Phase 9 Task 1).
//
// Binds the xHCI PCI function (class code 0x0C/0x03/0x30), maps BAR0 and
// brings up the controller through UsbStack, which enumerates connected
// devices and dispatches the HID / mass-storage / CDC class drivers.
// The controller is polled: UsbStack.Poll() runs from the shell idle
// pump (same cadence as the Phase 8 PCIe hot-plug scan) and drains the
// event ring, so hot-plug and interrupt endpoints work without MSI.

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Usb;
using ProtonOS.Usb.Xhci;

namespace ProtonOS.Drivers.Builtin;

/// <summary>xHCI (USB 3.x) host controller driver.</summary>
public sealed unsafe class XhciDriver : IDriver
{
    // Matches PCI class code 0x0C/0x03/0x30 packed as (base<<16)|(sub<<8)|progIF.
    private const uint XhciClassCode = 0x0C0330;

    private IDriverServices _services;
    private XhciController _controller;
    private ulong _mmio;
    private ulong _mmioLength;

    public string Name => "xhci";

    public string Version => "1.0.0";

    public int AbiMajor => DriverAbi.Major;

    public int AbiMinor => DriverAbi.Minor;

    public void Initialize(IDriverServices services)
    {
        _services = services;
    }

    /// <summary>xHCI host controllers (USB 3.x): PCI class 0x0C/0x03/0x30.</summary>
    public bool Match(DeviceInfo device)
    {
        if (device.Bus != "pci")
            return false;
        return device.ClassCode == XhciClassCode;
    }

    /// <summary>Requires a BAR window large enough for the register set.</summary>
    public bool Probe(DeviceInfo device)
    {
        DeviceResource mmio;
        return device.TryGetResource(DeviceResourceKind.Mmio, out mmio) && mmio.Length >= 0x1000;
    }

    /// <summary>Maps BAR0, resets and starts the controller, enumerates ports.</summary>
    public bool Start(DeviceInfo device)
    {
        if (_services == null)
            return false;

        DeviceResource mmio;
        if (!device.TryGetResource(DeviceResourceKind.Mmio, out mmio))
        {
            _services.Log(DriverLogLevel.Warning, "xhci: no MMIO resource");
            return false;
        }

        _mmio = _services.MapMmio(mmio.Base, mmio.Length);
        if (_mmio == 0)
        {
            _services.Log(DriverLogLevel.Error, "xhci: BAR0 mapping failed");
            return false;
        }
        _mmioLength = mmio.Length;

        _controller = new XhciController();
        var st = _controller.Init((byte*)_mmio);
        if (st != XhciStatus.Ok)
        {
            _services.Log(DriverLogLevel.Error, "xhci: controller init failed");
            _services.UnmapMmio(_mmio, _mmioLength);
            _mmio = 0;
            _controller = null;
            return false;
        }

        UsbStack.RegisterController(_controller);
        UsbStack.EnumerateAll();

        _services.Log(DriverLogLevel.Info, "xhci started (bar0 0x" + BuiltinHex.Hex(mmio.Base)
            + " ports=" + _controller.MaxPorts.ToString()
            + " devices=" + UsbStack.DeviceCount.ToString() + ")");
        return true;
    }

    public void Stop(DeviceInfo device)
    {
        _ = device;
        if (_services != null && _mmio != 0)
        {
            _services.UnmapMmio(_mmio, _mmioLength);
            _mmio = 0;
        }
        _controller = null;
    }
}
