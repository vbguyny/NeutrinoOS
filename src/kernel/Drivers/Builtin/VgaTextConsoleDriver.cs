// ProtonOS Kernel - built-in VGA text console driver.
//
// Binds to the PCI VGA-compatible display device (class 0x03/0x00, e.g. the
// QEMU stdvga 1234:1111) in the device tree and maps the card's MMIO BAR
// through IDriverServices. NOTE: the text console itself still renders via
// the legacy identity-mapped VGA text window at 0xB8000 (VgaTextDriver);
// switching the console to a linear-mode framebuffer is a follow-up. This
// driver currently provides framework ownership of the display device and
// the PCI MMIO mapping path.

using System;
using NeutrinoOS.Drivers;

namespace ProtonOS.Drivers.Builtin;

/// <summary>VGA-compatible display driver (PCI class 0x030000).</summary>
public sealed class VgaTextConsoleDriver : IDriver
{
    private static IDriverServices _services;
    private static ulong _framebuffer;
    private static ulong _framebufferSize;

    public string Name => "vga-text";

    public string Version => "1.0.0";

    public int AbiMajor => DriverAbi.Major;

    public int AbiMinor => DriverAbi.Minor;

    /// <summary>Stores the kernel services instance injected by the manager.</summary>
    public void Initialize(IDriverServices services)
    {
        _services = services;
    }

    /// <summary>Matches PCI VGA-compatible display controllers.</summary>
    public bool Match(DeviceInfo device)
    {
        return device.Bus == "pci"
            && device.Class == DeviceClass.Display
            && (device.ClassCode & 0xFFFF00) == 0x030000;
    }

    /// <summary>Require an MMIO BAR before binding.</summary>
    public bool Probe(DeviceInfo device)
    {
        DeviceResource mmio;
        return device.TryGetResource(DeviceResourceKind.Mmio, out mmio) && mmio.Length > 0;
    }

    /// <summary>Start: map the framebuffer BAR through the services ABI.</summary>
    public bool Start(DeviceInfo device)
    {
        if (_services == null)
            return false;

        DeviceResource mmio;
        if (!device.TryGetResource(DeviceResourceKind.Mmio, out mmio))
        {
            _services.Log(DriverLogLevel.Warning, "vga-text: no MMIO resource");
            return true;   // still claim the device; text console is legacy
        }

        _framebuffer = _services.MapMmio(mmio.Base, mmio.Length);
        _framebufferSize = mmio.Length;
        if (_framebuffer == 0)
        {
            _services.Log(DriverLogLevel.Error, "vga-text: MapMmio failed");
            return false;
        }

        _services.Log(DriverLogLevel.Info, "vga-text started (fb 0x" + Hex16(_framebuffer)
            + " len 0x" + Hex16(_framebufferSize) + "; text console uses legacy 0xB8000)");
        return true;
    }

    /// <summary>Stop: unmap the framebuffer.</summary>
    public void Stop(DeviceInfo device)
    {
        if (_services != null && _framebuffer != 0)
        {
            _services.UnmapMmio(_framebuffer, _framebufferSize);
            _framebuffer = 0;
        }
    }

    private static char HexDigit(int v)
    {
        v &= 0xF;
        return v < 10 ? (char)('0' + v) : (char)('A' + (v - 10));
    }

    private static string Hex16(ulong v)
    {
        char[] chars = new char[16];
        for (int i = 0; i < 16; i++)
        {
            chars[i] = HexDigit((int)(v >> (60 - i * 4)));
        }
        return new string(chars);
    }
}
