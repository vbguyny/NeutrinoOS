// NeutrinoDriver - template driver for NeutrinoOS.
//
// A driver implements IDriver and reaches hardware only through the
// IDriverServices instance handed to it by its host. Lifecycle:
//
//   Match(device)  - cheap test against every device; no hardware access
//   Probe(device)  - validate the exact variant/resources; may fail softly
//   Start(device)  - map resources, register interrupts, bring up hardware
//   Stop(device)   - release everything
//
// See docs/PHASE8-DRIVER.md for the framework overview.

using System;
using NeutrinoOS.Drivers;

namespace MyDriver
{
    /// <summary>Example driver: edit Name/Version, Match() and Start().</summary>
    public sealed class MyDeviceDriver : IDriver
    {
        private IDriverServices _services;
        private int _irq = -1;
        private ulong _mmio;

        public string Name => "my-driver";

        public string Version => "1.0.0";

        // ABI version this driver was built against (see DriverAbi).
        public int AbiMajor => DriverAbi.Major;
        public int AbiMinor => DriverAbi.Minor;

        /// <summary>
        /// Kernel driver-package loader convention: installed drivers are
        /// instantiated through this static parameterless factory (it runs
        /// in the driver's own assembly, so `new` happens JIT-side).
        /// </summary>
        public static IDriver Create()
        {
            return new MyDeviceDriver();
        }

        /// <summary>Store the host-injected kernel services instance.</summary>
        public void Initialize(IDriverServices services)
        {
            _services = services;
        }

        /// <summary>
        /// Cheap match. Example: a PCI device by vendor/device id.
        /// Keep this allocation-free and free of hardware access.
        /// </summary>
        public bool Match(DeviceInfo device)
        {
            // Match the QEMU e1000 as an example:
            // return device.Bus == "pci" && device.VendorId == 0x8086 && device.DeviceId == 0x10D3;

            // Or match by device class:
            // return device.Class == DeviceClass.Network;
            return false;
        }

        /// <summary>Validate resources before binding; return false to defer.</summary>
        public bool Probe(DeviceInfo device)
        {
            DeviceResource mmio;
            return device.TryGetResource(DeviceResourceKind.Mmio, out mmio);
        }

        /// <summary>Bring the device up through kernel services.</summary>
        public bool Start(DeviceInfo device)
        {
            if (_services == null)
                return false;

            DeviceResource mmio;
            if (device.TryGetResource(DeviceResourceKind.Mmio, out mmio))
            {
                _mmio = _services.MapMmio(mmio.Base, mmio.Length);
                if (_mmio == 0)
                    return false;
            }

            // Example: claim IRQ 10 (adapt to your device).
            // DeviceResource irq;
            // if (device.TryGetResource(DeviceResourceKind.Irq, out irq))
            // {
            //     _irq = (int)irq.Base;
            //     if (!_services.RegisterInterrupt(_irq, OnInterrupt))
            //         return false;
            // }

            _services.Log(DriverLogLevel.Info, "my-driver started on " + device.Path);
            return true;
        }

        /// <summary>Release resources. Must tolerate being called after a
        /// partially failed Start.</summary>
        public void Stop(DeviceInfo device)
        {
            _ = device;
            if (_services != null && _irq >= 0)
                _services.UnregisterInterrupt(_irq);
            _irq = -1;
            _mmio = 0;
        }

        private static void OnInterrupt(int irq)
        {
            _ = irq;
            // Keep interrupt handlers short: acknowledge the device and
            // defer real work to a service tick.
        }
    }
}
