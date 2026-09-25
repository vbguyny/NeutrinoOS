// ProtonOS Kernel - built-in PS/2 keyboard driver (8042 controller).
//
// Binds to the platform ps2 device (I/O 0x60/0x64, IRQ1 keyboard + IRQ12
// mouse) in the device tree. Through IDriverServices the driver owns the
// keyboard IRQ1 registration; scancode processing stays in Ps2Keyboard
// (shared with the console layer, which must run before the framework is
// up).

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Platform;

namespace ProtonOS.Drivers.Builtin;

/// <summary>PS/2 keyboard driver (8042 controller, IRQ1).</summary>
public sealed class Ps2KeyboardDriver : IDriver
{
    private static IDriverServices _services;
    private static bool _irqRegistered;

    public string Name => "ps2-keyboard";

    public string Version => "1.0.0";

    public int AbiMajor => DriverAbi.Major;

    public int AbiMinor => DriverAbi.Minor;

    /// <summary>Stores the kernel services instance injected by the manager.</summary>
    public void Initialize(IDriverServices services)
    {
        _services = services;
    }

    /// <summary>Matches the platform ps2 device (vid 0xFFFF, did 0x8042).</summary>
    public bool Match(DeviceInfo device)
    {
        return device.Bus == "platform" && device.DeviceId == 0x8042;
    }

    /// <summary>Require the 8042 I/O port range before binding.</summary>
    public bool Probe(DeviceInfo device)
    {
        DeviceResource port;
        return device.TryGetResource(DeviceResourceKind.IoPort, out port) && port.Length >= 5;
    }

    /// <summary>
    /// Start: take over IRQ1 registration for the keyboard through the
    /// driver services ABI. The 8042 controller itself (and IRQ12 masking)
    /// is configured by Ps2Keyboard.Initialize, which the console layer
    /// runs before the framework exists.
    /// </summary>
    public bool Start(DeviceInfo device)
    {
        if (_services == null)
            return false;

        DeviceResource irqRes;
        if (device.TryGetResource(DeviceResourceKind.Irq, out irqRes))
        {
            int irq = (int)irqRes.Base;   // first IRQ resource = keyboard (IRQ1)
            if (!_services.RegisterInterrupt(irq, HandleIrq))
            {
                _services.Log(DriverLogLevel.Error, "ps2-keyboard: failed to register IRQ");
                return false;
            }
            _irqRegistered = true;
            _services.Log(DriverLogLevel.Info, "ps2-keyboard started (IRQ " + irq.ToString() + ")");
        }
        else
        {
            _services.Log(DriverLogLevel.Warning, "ps2-keyboard: no IRQ resource");
        }
        return true;
    }

    /// <summary>Stop: release the keyboard IRQ line.</summary>
    public void Stop(DeviceInfo device)
    {
        if (_services != null && _irqRegistered)
        {
            _services.UnregisterInterrupt(1);
            _irqRegistered = false;
        }
    }

    /// <summary>Interrupt callback: run the shared scancode drain (thunk EOIs).</summary>
    private static void HandleIrq(int irq)
    {
        _ = irq;
        Ps2Keyboard.HandleInterruptBody();
    }
}
