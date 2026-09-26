// hello-driver - a minimal virtual LED driver for NeutrinoOS.
//
// The sample matches a virtual platform LED ("platform/led0" with an MMIO
// register), lights it on Start(), logs through the kernel services and
// exposes /dev/led0. See docs/PHASE8-DRIVER.md for the framework overview
// and docs/SDK-PACKAGING.md for packaging (manifest-based driver pack).

using System;
using NeutrinoOS.Drivers;

namespace HelloDriver
{
    public sealed unsafe class MyLedDriver : IDriver
    {
        private const uint LedOn = 1;
        private const uint LedOff = 0;

        private IDriverServices _services;
        private ulong _mmio;
        private bool _nodeCreated;

        public string Name => "hello-led";

        public string Version => "1.0.0";

        public int AbiMajor => DriverAbi.Major;
        public int AbiMinor => DriverAbi.Minor;

        /// <summary>Kernel driver-package loader factory (see PHASE8-DRIVER.md).</summary>
        public static IDriver Create()
        {
            return new MyLedDriver();
        }

        public void Initialize(IDriverServices services)
        {
            _services = services;
        }

        /// <summary>Cheap, allocation-free match: the virtual LED platform device.</summary>
        public bool Match(DeviceInfo device)
        {
            return device.Bus == "platform" && device.Path != null && device.Path.EndsWith("led0");
        }

        /// <summary>Require the single MMIO register.</summary>
        public bool Probe(DeviceInfo device)
        {
            DeviceResource mmio;
            return device.TryGetResource(DeviceResourceKind.Mmio, out mmio) && mmio.Length >= 4;
        }

        public bool Start(DeviceInfo device)
        {
            if (_services == null)
                return false;

            DeviceResource mmio;
            if (!device.TryGetResource(DeviceResourceKind.Mmio, out mmio))
                return false;

            _mmio = _services.MapMmio(mmio.Base, mmio.Length);
            if (_mmio == 0)
            {
                _services.Log(DriverLogLevel.Error, "hello-led: MMIO map failed");
                return false;
            }

            *(uint*)_mmio = LedOn;
            _services.Log(DriverLogLevel.Info, "hello-led: LED on (" + device.Path + ")");

            string node = _services.CreateDeviceNode("led0", 240, 0);
            _nodeCreated = node != null;
            if (_nodeCreated)
                _services.Log(DriverLogLevel.Info, "hello-led: device node " + node);
            return true;
        }

        public void Stop(DeviceInfo device)
        {
            _ = device;
            if (_services == null)
                return;

            if (_mmio != 0)
            {
                *(uint*)_mmio = LedOff;
                _services.UnmapMmio(_mmio, 4);
                _mmio = 0;
                _services.Log(DriverLogLevel.Info, "hello-led: LED off");
            }
            if (_nodeCreated)
            {
                _services.RemoveDeviceNode("led0");
                _nodeCreated = false;
            }
        }
    }
}
