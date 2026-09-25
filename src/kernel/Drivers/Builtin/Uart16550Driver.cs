// ProtonOS Kernel - built-in UART 16550 driver (first driver on the new
// framework).
//
// Binds to the legacy platform COM1 device (I/O 0x3F8, IRQ4) in the device
// tree. Through IDriverServices it owns the IRQ registration for the serial
// port; the receive/transmit logic itself stays in Uart16550 (shared with
// the boot console, which must run before the framework is up).

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Platform;

namespace ProtonOS.Drivers.Builtin;

/// <summary>16550-compatible serial port driver (COM1).</summary>
public sealed class Uart16550Driver : IDriver
{
    private static IDriverServices _services;
    private static int _irq;

    public string Name => "uart16550";

    public string Version => "1.0.0";

    public int AbiMajor => DriverAbi.Major;

    public int AbiMinor => DriverAbi.Minor;

    /// <summary>Matches the platform uart0 device (vid 0xFFFF, did 0x1650).</summary>
    public bool Match(DeviceInfo device)
    {
        return device.Bus == "platform" && device.DeviceId == 0x1650;
    }

    /// <summary>Require an I/O port resource before binding.</summary>
    public bool Probe(DeviceInfo device)
    {
        DeviceResource port;
        return device.TryGetResource(DeviceResourceKind.IoPort, out port) && port.Length >= 8;
    }

    /// <summary>
    /// Start: take over IRQ registration for the port through the driver
    /// services ABI. The kernel's legacy registration stays in place to
    /// cover the window before the framework is initialized; the services
    /// registration replaces it (same vector) so the driver owns the line
    /// from here on.
    /// </summary>
    public bool Start(DeviceInfo device)
    {
        _services = KernelDriverServices.Instance;

        DeviceResource irqRes;
        if (device.TryGetResource(DeviceResourceKind.Irq, out irqRes))
        {
            _irq = (int)irqRes.Base;
            if (!_services.RegisterInterrupt(_irq, HandleIrq))
            {
                _services.Log(DriverLogLevel.Error, "uart16550: failed to register IRQ");
                return false;
            }
        }

        // Ensure RX/TX interrupt enables are on (idempotent).
        Uart16550.EnableInterrupts();

        _services.Log(DriverLogLevel.Info, "uart16550 started (IRQ " + _irq.ToString() + ")");
        return true;
    }

    /// <summary>Stop: release the IRQ line and disable port interrupts.</summary>
    public void Stop(DeviceInfo device)
    {
        Uart16550.DisableInterrupts();
        if (_services != null && _irq >= 0)
            _services.UnregisterInterrupt(_irq);
        _irq = -1;
    }

    /// <summary>Interrupt callback: run the shared UART IRQ body (thunk EOIs).</summary>
    private static void HandleIrq(int irq)
    {
        _ = irq;
        Uart16550.HandleInterruptBody();
    }
}
